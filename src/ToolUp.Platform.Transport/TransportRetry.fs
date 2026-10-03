// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

// ─── Retry loop + one-call send (Phase 128) ──────────────────────
//
// The retry loop, generic in its error type: the caller says how a
// failure classifies (`RetryClass` — data, not an `OnFailure` callback;
// GP 12 rule 3) and how the final failure is wrapped, and the loop owns
// attempts, exponential backoff and exhaustion. Every retrying caller in
// the SDK runs on it, so the loop exists once.
//
// The only effect is `Async.Sleep`, which Fable supports.

namespace ToolUp.Platform.Transport

open System

/// How the retry loop treats one failure.
[<RequireQualifiedAccess>]
type RetryClass =
    /// Retrying cannot help; surface the failure now.
    | Permanent
    /// Retry after the policy's backoff.
    | Transient
    /// Retry, but not before `minimumWait` — the provider's `Retry-After`.
    /// A hint longer than the policy's `MaxBackoff` ends the loop instead:
    /// the cap exists so a wedged dependency does not park the caller.
    | TransientAfter of minimumWait: TimeSpan

module TransportRetry =

    /// The retry class of a `TransportError`.
    let classOf (error: TransportError) : RetryClass =
        match error with
        | TransportError.Transient(_, _, Some wait) -> RetryClass.TransientAfter wait
        | error when TransportError.isRetryable error -> RetryClass.Transient
        | _ -> RetryClass.Permanent

    /// Run `attempt` under `policy`. Semantics (the Phase 251 contract):
    ///   * `Ok` returns immediately;
    ///   * a `Permanent` failure propagates immediately, unwrapped;
    ///   * once `MaxAttempts` is reached the last failure is wrapped by
    ///     `exhausted attempts lastError` — UNLESS `MaxAttempts = 1`, where
    ///     the raw failure surfaces (1 = no retries);
    ///   * a `TransientAfter` hint beyond `MaxBackoff` is wrapped by
    ///     `exhausted` at once rather than waited out;
    ///   * otherwise it waits `RetryPolicy.delayFor policy (n+1)` — or the
    ///     provider's hint, when longer — and tries again.
    let runWith
        (classify: 'E -> RetryClass)
        (exhausted: int -> 'E -> 'E)
        (policy: RetryPolicy)
        (attempt: unit -> Async<Result<'T, 'E>>)
        : Async<Result<'T, 'E>> =
        let rec loop attemptsMade = async {
            let! result = attempt ()
            let attemptsMade = attemptsMade + 1

            match result with
            | Ok value -> return Ok value
            | Error err ->
                match classify err with
                | RetryClass.Permanent -> return Error err
                | _ when attemptsMade >= policy.MaxAttempts ->
                    return
                        if policy.MaxAttempts = 1 then
                            Error err
                        else
                            Error(exhausted attemptsMade err)
                | RetryClass.TransientAfter wait when wait > policy.MaxBackoff ->
                    return Error(exhausted attemptsMade err)
                | retryClass ->
                    let backoff = RetryPolicy.delayFor policy (attemptsMade + 1)

                    let delay =
                        match retryClass with
                        | RetryClass.TransientAfter wait when wait > backoff -> wait
                        | _ -> backoff

                    // int-ms overload — Fable's Async.Sleep only takes ms.
                    do! Async.Sleep(int delay.TotalMilliseconds)
                    return! loop attemptsMade
        }

        loop 0

    /// `runWith` over `TransportError`, honouring `Retry-After`.
    let run
        (policy: RetryPolicy)
        (attempt: unit -> Async<Result<'T, TransportError>>)
        : Async<Result<'T, TransportError>> =
        runWith classOf (fun attempts last -> TransportError.Exhausted(attempts, last)) policy attempt

/// One outbound call over the injected `IHttpTransport`, classified.
module HttpCall =

    /// A bodiless GET request — the shape a paged third-party API read
    /// uses. `url` may be relative to the transport's base address or
    /// absolute (a provider's next-page link).
    let get (url: string) (headers: (string * string) list) : HttpRequest = {
        Method = "GET"
        Url = url
        Headers = headers
        Body = None
    }

    /// One attempt: a 2xx is `Ok`, any other status is classified by
    /// `TransportClassifier.classifyResponse`, and an exception from the
    /// transport (it raises on connection failure) is `Network`.
    let attempt (transport: IHttpTransport) (request: HttpRequest) : Async<Result<HttpResponse, TransportError>> = async {
        try
            let! response = transport.Send request

            return
                if HttpResponse.isSuccess response then
                    Ok response
                else
                    Error(TransportClassifier.classifyResponse response)
        with ex ->
            return Error(TransportClassifier.classifyTransportFailure ex.Message)
    }

    /// The call under a retry policy — retry expressed entirely as data.
    let send
        (policy: RetryPolicy)
        (transport: IHttpTransport)
        (request: HttpRequest)
        : Async<Result<HttpResponse, TransportError>> =
        TransportRetry.run policy (fun () -> attempt transport request)