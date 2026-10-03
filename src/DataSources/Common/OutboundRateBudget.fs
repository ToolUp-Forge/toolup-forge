// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.DataSources.Common

open System
open System.Threading
open ToolUp.Platform
open ToolUp.Platform.Transport

// ─── Phase 128 — quota-aware outbound HTTP ───────────────────────
//
// A third-party API publishes its quota three ways: a requests-per-window
// ceiling, an in-flight ceiling, and — when either is crossed anyway — a
// 429 with `Retry-After`. `OutboundRateBudget` declares all three as data
// and `OutboundRateBudget.decorate` enforces them around any
// `IHttpTransport`, so a connector is quota-safe by composition rather
// than by remembering to call a limiter at each emission site.
//
// The requests-per-window half is NOT re-implemented here: it is the
// Phase 9v outbound substrate. `Window` is a `RateLimitDescriptor` — the
// same record a companion registers with `ServerApp.withRateLimitDescriptor`
// — and the decorator draws every call from the composed `IRateLimiter`
// (soft ceiling, long-window `Refused`, per-scope or per-provider
// fairness, and the Redis-backed swap all come with it). This file adds
// only what that substrate does not model: the concurrency cap and the
// provider-requested pause. It is the outbound mirror of the inbound
// limiter (`InboundRateLimitTypes.fs`): a declaration, not a callback.
//
// A refusal — the limiter's long-window `Refused`, or a provider pause
// longer than the budget will wait — is answered locally with a
// synthesised 429 carrying `TransportClassifier.RefusedHeader`, which
// classifies as `TransportError.Refused`: not retried, surfaced as data.
//
// State: the concurrency slots and the pause deadline belong to one
// decorated instance — process-local, the same posture as the in-process
// limiter. Share one decorated transport across the calls that should
// share the budget (one per connector and scope).

/// How a provider's `429 Retry-After` is treated.
[<RequireQualifiedAccess>]
type RetryAfterHandling =
    /// Pass the 429 through untouched; only the retry policy reacts.
    | Ignore
    /// Hold every following call through this transport until the pause
    /// elapses — unless it is longer than `maxWait`, in which case calls
    /// are refused until it elapses.
    | HoldUpTo of maxWait: TimeSpan

/// The declared outbound budget for one provider.
type OutboundRateBudget = {
    /// Requests per window (and the optional long window), as the Phase 9v
    /// descriptor. Register the same value with the composed `IRateLimiter`
    /// (`ServerApp.withRateLimitDescriptor`) — an unregistered provider is
    /// admitted unthrottled, by that substrate's fail-open design.
    Window: RateLimitDescriptor
    /// Most requests in flight at once through one decorated transport.
    /// `None` = no cap.
    MaxConcurrency: int option
    /// Treatment of the provider's own 429 pause.
    RetryAfter: RetryAfterHandling
}

[<RequireQualifiedAccess>]
module OutboundRateBudget =

    /// A budget over `window` with no concurrency cap, honouring provider
    /// pauses of up to a minute.
    let ofWindow (window: RateLimitDescriptor) : OutboundRateBudget = {
        Window = window
        MaxConcurrency = None
        RetryAfter = RetryAfterHandling.HoldUpTo(TimeSpan.FromMinutes 1.0)
    }

    /// Reject a budget no transport could honour.
    let validate (budget: OutboundRateBudget) : Result<OutboundRateBudget, string> =
        match budget with
        | { MaxConcurrency = Some n } when n < 1 -> Error $"MaxConcurrency must be at least 1 (got {n})"
        | {
              RetryAfter = RetryAfterHandling.HoldUpTo wait
          } when wait < TimeSpan.Zero -> Error "RetryAfter.HoldUpTo must not be negative"
        | { Window = window } when String.IsNullOrWhiteSpace window.Provider ->
            Error "Window.Provider must name the provider"
        | _ -> Ok budget

    /// The locally synthesised refusal — a 429 the classifier reads as
    /// `TransportError.Refused` with `reason` as its message.
    let refusal (reason: string) : HttpResponse = {
        StatusCode = 429
        Headers = [ TransportClassifier.RefusedHeader, "true" ]
        Body = reason
    }

    /// Enforce `budget` around `inner` for calls on behalf of `scopeId`
    /// (`subKey` partitions further — a per-property or per-account quota).
    /// `clock` reads the time the provider's pause is measured against.
    /// Raises `ArgumentException` for a budget `validate` rejects.
    let decorate
        (limiter: IRateLimiter)
        (clock: TimeProvider)
        (budget: OutboundRateBudget)
        (scopeId: string)
        (subKey: string option)
        (inner: IHttpTransport)
        : IHttpTransport =
        match validate budget with
        | Error message -> invalidArg (nameof budget) message
        | Ok _ -> ()

        let key = {
            ScopeId = scopeId
            Provider = budget.Window.Provider
            SubKey = subKey
        }

        let slots = budget.MaxConcurrency |> Option.map (fun n -> new SemaphoreSlim(n, n))

        let gate = obj ()
        let mutable pausedUntil = DateTimeOffset.MinValue

        let readPause () = lock gate (fun () -> pausedUntil)

        let extendPause (until: DateTimeOffset) =
            lock gate (fun () ->
                if until > pausedUntil then
                    pausedUntil <- until)

        let admitted (request: HttpRequest) = async {
            // A provider-requested pause first: it is the provider's own
            // statement that the window is shut.
            let remaining = readPause () - clock.GetUtcNow()

            let pause =
                match budget.RetryAfter with
                | RetryAfterHandling.HoldUpTo maxWait when remaining > maxWait ->
                    Error(
                        $"provider {budget.Window.Provider} asked for a pause of {int (ceil remaining.TotalSeconds)} s, longer than this budget waits ({int maxWait.TotalSeconds} s)"
                    )
                | RetryAfterHandling.HoldUpTo _ when remaining > TimeSpan.Zero -> Ok remaining
                | _ -> Ok TimeSpan.Zero

            match pause with
            | Error reason -> return refusal reason
            | Ok wait ->
                if wait > TimeSpan.Zero then
                    do! Async.Sleep wait

                let! decision = limiter.Wait key

                match decision with
                | Refused reason -> return refusal reason
                | Proceed
                | DelayedBy _ ->
                    let! response = inner.Send request

                    match budget.RetryAfter, response.StatusCode with
                    | RetryAfterHandling.HoldUpTo _, 429 ->
                        match TransportClassifier.tryRetryAfter response.Headers with
                        | Some hint -> extendPause (clock.GetUtcNow() + hint)
                        | None -> ()
                    | _ -> ()

                    return response
        }

        { new IHttpTransport with
            member _.Send request = async {
                match slots with
                | None -> return! admitted request
                | Some semaphore ->
                    do! semaphore.WaitAsync() |> Async.AwaitTask

                    try
                        return! admitted request
                    finally
                        semaphore.Release() |> ignore
            }
        }