// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.AI

open ToolUp.Platform // RetryPolicy (relocated alongside, namespace ToolUp.Platform)

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
// The only effect is `Async.Sleep` between attempts, which Fable supports,
// so this file Fable-compiles (GP 7 — the async is at the boundary; GP 8 —
// Fable native).

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
        let rec loop attemptsMade = async {
            let! result = singleAttempt ()
            let attemptsMade = attemptsMade + 1

            match result with
            | Ok r -> return Ok r
            | Error err when not (AIProviderError.isRetryable err) -> return Error err
            | Error err when attemptsMade >= policy.MaxAttempts ->
                return
                    if policy.MaxAttempts = 1 then
                        Error err
                    else
                        Error(RetriesExhausted(attemptsMade, err))
            | Error _ ->
                let delay = RetryPolicy.delayFor policy (attemptsMade + 1)
                // int-ms overload (not the TimeSpan one) — Fable's Async.Sleep
                // only accepts milliseconds; the values are whole ms so this is
                // behaviourally identical to the providers' `Async.Sleep delay`.
                do! Async.Sleep(int delay.TotalMilliseconds)
                return! loop attemptsMade
        }

        loop 0