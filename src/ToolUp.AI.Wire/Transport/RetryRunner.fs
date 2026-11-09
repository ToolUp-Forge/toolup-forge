// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.AI

open ToolUp.Platform.Transport

// ─── The AI taxonomy over the one retry loop ─────────────────────
//
// `TransportRetry.runPausing` owns attempts, backoff, the rate-limit floor
// and exhaustion; this module says how an `AIProviderError` classifies and
// how the last one is wrapped. Generic in the success type, so it is
// blind to whether the attempt is a buffered or a streamed response.

module RetryRunner =

    /// How the loop treats one `AIProviderError`: a 429 is a rate limit
    /// (the policy's `RateLimitFloor` applies), any other retryable error is
    /// transient, and everything else is permanent.
    let classify (err: AIProviderError) : RetryClass =
        match err with
        | TransientServer(429, _) -> RetryClass.RateLimited
        | err when AIProviderError.isRetryable err -> RetryClass.Transient
        | _ -> RetryClass.Permanent

    /// Run `singleAttempt` under `policy`, pausing through `sleep`:
    ///   * `Ok` returns immediately;
    ///   * a non-retryable error propagates immediately;
    ///   * once `MaxAttempts` is reached, the last error wraps as
    ///     `RetriesExhausted(attempts, lastError)` — UNLESS `MaxAttempts = 1`
    ///     (`MaxAttempts` counts the first attempt itself, so 1 = no retries
    ///     and the raw error surfaces);
    ///   * a 429 waits `RetryPolicy.rateLimitDelay`; any other retryable
    ///     error waits `delayFor policy (n+1)`.
    let runPausing
        (sleep: System.TimeSpan -> Async<unit>)
        (policy: RetryPolicy)
        (singleAttempt: unit -> Async<Result<'T, AIProviderError>>)
        : Async<Result<'T, AIProviderError>> =
        TransportRetry.runPausing
            sleep
            classify
            (fun attempts last -> RetriesExhausted(attempts, last))
            policy
            singleAttempt

    /// `runPausing` over `Async.Sleep`.
    let run
        (policy: RetryPolicy)
        (singleAttempt: unit -> Async<Result<'T, AIProviderError>>)
        : Async<Result<'T, AIProviderError>> =
        runPausing (fun delay -> Async.Sleep(int delay.TotalMilliseconds)) policy singleAttempt