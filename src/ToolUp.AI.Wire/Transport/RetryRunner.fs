// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.AI

open ToolUp.Platform.Transport // RetryPolicy, TransportRetry, RetryClass (Phase 128)

// ─── Pure portable retry loop (Wave 32, Phase 251) ───────────────
//
// The `singleAttempt` / `retryLoop` recursion currently duplicated
// verbatim in every provider's `SendMessage` / `SendStructuredMessage`,
// lifted to one generic pure function. It is parameterised over a single
// attempt (`unit -> Async<Result<'T, AIProviderError>>`) — the per-provider
// mapper supplies the attempt; the runner owns the retry/backoff/exhaustion
// policy. Generic in the success type `'T`, so it is host-agnostic and
// blind to whether the attempt is a buffered or streamed response.
//
// Phase 128 — the loop itself is now the connector-neutral
// `TransportRetry.runWith`; this module supplies the AI taxonomy's
// classification and exhaustion wrapper. Behaviour is unchanged.

module RetryRunner =

    /// Run `singleAttempt` under `policy`, retrying on retryable errors
    /// with exponential backoff (`RetryPolicy.delayFor`, capped at
    /// `MaxBackoff`). Semantics byte-identical to the inline provider loop:
    ///   * `Ok` returns immediately;
    ///   * a non-retryable error (`AIProviderError.isRetryable = false`)
    ///     propagates immediately;
    ///   * once `MaxAttempts` is reached, the last error wraps as
    ///     `RetriesExhausted(attempts, lastError)` — UNLESS `MaxAttempts = 1`
    ///     (the post-11.C.5 fail-fast contract: `MaxAttempts` counts the
    ///     first attempt itself, so 1 = no retries and the raw error
    ///     surfaces, not a one-attempt `RetriesExhausted`);
    ///   * otherwise it sleeps `delayFor policy (n+1)` and retries.
    let run
        (policy: RetryPolicy)
        (singleAttempt: unit -> Async<Result<'T, AIProviderError>>)
        : Async<Result<'T, AIProviderError>> =
        TransportRetry.runWith
            (fun err ->
                if AIProviderError.isRetryable err then
                    RetryClass.Transient
                else
                    RetryClass.Permanent)
            (fun attempts last -> RetriesExhausted(attempts, last))
            policy
            singleAttempt