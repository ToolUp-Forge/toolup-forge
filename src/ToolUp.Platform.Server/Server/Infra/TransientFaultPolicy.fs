// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.TransientFault

open System

// ─── Phase 176 — transient-fault-handling policy + shared runner ─────
//
// A portable retry / circuit-breaker / per-call-timeout layer over the
// cloud-latency SDK interfaces (`IBlobStorage`, `ISecretStore`, …). The
// policy is expressed as a record — retry-as-data, satisfying the GP 12
// portability rule 3 (retry/backoff/breaker behaviour is *data*, never a
// framework callback). A thin decorator wraps any existing implementation
// and re-exposes the same interface, so correlation/context rides the
// wrapped async chain unchanged (GP 7) and a deployment that doesn't opt
// in pays nothing (GP 11/13).
//
// **Faults are exceptions, not `Result.Error` values.** Transient cloud
// failures surface as thrown exceptions (vendor-SDK socket/HTTP/timeout
// faults); the `RetryClassifier` decides which of *those* are worth
// retrying. A deterministic domain outcome that an interface returns as
// `Result.Error` (a `NotFound` download, a `"read-only"` secret write) is
// a *value* — it never reaches the classifier and is never retried.

/// Delay schedule between retry attempts.
type BackoffSchedule =
    /// No wait — retries fire back-to-back.
    | NoBackoff
    /// A constant wait before every retry.
    | Fixed of TimeSpan
    /// `baseDelay * 2^attempt`, clamped to `maxDelay`.
    | Exponential of baseDelay: TimeSpan * maxDelay: TimeSpan

module BackoffSchedule =
    /// Delay before the retry that follows attempt `attempt` (0-based:
    /// `delayFor schedule 0` is the wait after the *first* failed attempt,
    /// `delayFor schedule 1` after the second, …). Exponential growth is
    /// overflow-guarded — a large attempt count clamps to `maxDelay`
    /// rather than wrapping the bit-shift.
    let delayFor (schedule: BackoffSchedule) (attempt: int) : TimeSpan =
        match schedule with
        | NoBackoff -> TimeSpan.Zero
        | Fixed t -> t
        | Exponential(baseDelay, maxDelay) ->
            // Guard the doubling so a runaway attempt count can't overflow
            // the shift (or the double multiply) — clamp straight to max.
            let factor =
                if attempt >= 30 then
                    Double.PositiveInfinity
                else
                    float (1L <<< attempt)

            let ms = baseDelay.TotalMilliseconds * factor

            if Double.IsInfinity ms || ms >= maxDelay.TotalMilliseconds then
                maxDelay
            else
                TimeSpan.FromMilliseconds ms

/// Circuit-breaker policy.
type BreakerPolicy =
    /// No breaker — every call is attempted regardless of recent failures.
    | NoBreaker
    /// After `failures` consecutive terminal failures the breaker opens
    /// and short-circuits subsequent calls for `cooldown`; a success (or
    /// the first call after the cooldown elapses) resets it.
    | Threshold of failures: int * cooldown: TimeSpan

/// The transient-fault policy applied by a decorator. Retry-as-data
/// (GP 12 rule 3): every knob is a value on this record, never a
/// framework callback that would leak engine semantics.
type TransientFaultPolicy = {
    /// Maximum retries *after* the first attempt. `0` = a single attempt.
    MaxRetries: int
    /// Wait schedule between retries.
    Backoff: BackoffSchedule
    /// Circuit-breaker policy.
    Breaker: BreakerPolicy
    /// Per-call timeout. `Some t` cancels a call that hasn't completed in
    /// `t` (surfacing a `TimeoutException`, itself subject to the retry
    /// loop / classifier); `None` waits indefinitely on the inner call.
    PerCallTimeout: TimeSpan option
    /// Decides whether a thrown exception is a genuinely-transient fault
    /// worth retrying. A `false` verdict surfaces the exception
    /// immediately. Deterministic `Result.Error` outcomes never reach
    /// here — they are values, not exceptions.
    RetryClassifier: exn -> bool
}

/// Raised when a call is short-circuited because the circuit breaker is
/// open (it tripped on a prior burst of transient failures and is inside
/// its cooldown window). Distinct from the underlying fault so a caller
/// or test can tell "the breaker refused" from "the op itself failed".
exception CircuitOpenException of cooldownRemaining: TimeSpan

module TransientFaultPolicy =
    /// The no-op policy: a single attempt, no backoff, no breaker, no
    /// timeout, and a classifier that retries nothing. A decorator built
    /// on this is observably a pass-through (GP 11/13) — see
    /// `TransientFaultRunner` below, whose fast path returns the inner
    /// `Async` unchanged under this policy.
    let identity: TransientFaultPolicy = {
        MaxRetries = 0
        Backoff = NoBackoff
        Breaker = NoBreaker
        PerCallTimeout = None
        RetryClassifier = fun _ -> false
    }

    /// A conservative default classifier for cloud-vendor transient
    /// faults: per-call timeouts, transient socket / HTTP / IO failures.
    /// Errors that indicate a deterministic problem (bad argument,
    /// unauthorised) are NOT retried. Provided for convenience — a
    /// deployment may supply its own classifier tuned to a specific
    /// vendor SDK's exception taxonomy. Walks the `InnerException` chain
    /// so a wrapped transient cause is still recognised.
    let rec defaultTransientClassifier (ex: exn) : bool =
        match ex with
        | :? TimeoutException -> true
        | :? System.Net.Http.HttpRequestException -> true
        | :? System.Net.Sockets.SocketException -> true
        | :? System.IO.IOException -> true
        | :? OperationCanceledException -> true
        | _ when not (isNull ex.InnerException) -> defaultTransientClassifier ex.InnerException
        | _ -> false

/// How a deployment opts a cloud-latency interface into resilience.
/// `NoResilience` (the default) resolves to the bare implementation — no
/// decorator object in the hot path at all, byte-for-byte unchanged
/// (GP 13). `WithResiliencePolicy` wraps it.
type ResilienceMode =
    | NoResilience
    | WithResiliencePolicy of TransientFaultPolicy

/// Shared retry / circuit-breaker / per-call-timeout engine. One instance
/// is held per decorated store (it carries the breaker's consecutive-
/// failure tally + open-until instant). Handlers stay stateless between
/// invocations (GP 12 rule 4) — the only state here is the breaker's own
/// bookkeeping, the documented GP 5 mutable exception, guarded by a lock.
///
/// The `now` clock is injectable so breaker-cooldown behaviour can be
/// unit-tested deterministically without wall-clock sleeps; production
/// callers omit it (defaults to `DateTimeOffset.UtcNow`).
type TransientFaultRunner(policy: TransientFaultPolicy, ?now: unit -> DateTimeOffset) =
    let clock = defaultArg now (fun () -> DateTimeOffset.UtcNow)

    // With the identity (no-op) policy the runner returns the inner Async
    // unchanged — same object, no lock, no allocation, no added latency
    // (GP 13). Any non-trivial knob disables the fast path.
    let isPassThrough =
        policy.MaxRetries <= 0
        && policy.PerCallTimeout.IsNone
        && (match policy.Breaker with
            | NoBreaker -> true
            | Threshold _ -> false)

    let gate = obj ()
    let mutable consecutiveFailures = 0
    let mutable openUntil: DateTimeOffset option = None

    /// Refuse the call when the breaker is open and still inside cooldown;
    /// when the cooldown has elapsed, half-open (clear the latch + tally)
    /// and let this call through as the trial.
    let breakerGuard () =
        match policy.Breaker with
        | NoBreaker -> ()
        | Threshold _ ->
            lock gate (fun () ->
                match openUntil with
                | Some until ->
                    let remaining = until - clock ()

                    if remaining > TimeSpan.Zero then
                        raise (CircuitOpenException remaining)
                    else
                        openUntil <- None
                        consecutiveFailures <- 0
                | None -> ())

    let recordSuccess () =
        match policy.Breaker with
        | NoBreaker -> ()
        | Threshold _ ->
            lock gate (fun () ->
                consecutiveFailures <- 0
                openUntil <- None)

    /// A terminal failure (the call exhausted its retries) advances the
    /// breaker tally; reaching the threshold opens the breaker.
    let recordTerminalFailure () =
        match policy.Breaker with
        | NoBreaker -> ()
        | Threshold(failures, cooldown) ->
            lock gate (fun () ->
                consecutiveFailures <- consecutiveFailures + 1

                if consecutiveFailures >= failures then
                    openUntil <- Some(clock () + cooldown))

    let runWithTimeout (op: unit -> Async<'T>) : Async<'T> =
        match policy.PerCallTimeout with
        | None -> op ()
        | Some t -> async {
            let ms = max 1 (int (min (float Int32.MaxValue) t.TotalMilliseconds))
            // StartChild ties the child to this computation's cancellation
            // token and raises TimeoutException (cancelling the child) when
            // the budget elapses — so a hung inner call is actually cut.
            let! child = Async.StartChild(op (), ms)
            return! child
          }

    /// The policy this runner enforces (exposed for diagnostics / tests).
    member _.Policy = policy

    /// Run `op` under the policy. Returns the inner `Async` untouched on
    /// the identity fast path; otherwise applies breaker → timeout →
    /// retry/backoff.
    member _.Run(op: unit -> Async<'T>) : Async<'T> =
        if isPassThrough then
            op ()
        else
            let rec attemptLoop (attempt: int) : Async<'T> = async {
                try
                    let! r = runWithTimeout op
                    recordSuccess ()
                    return r
                with ex ->
                    if policy.RetryClassifier ex && attempt < policy.MaxRetries then
                        let delay = BackoffSchedule.delayFor policy.Backoff attempt

                        if delay > TimeSpan.Zero then
                            do! Async.Sleep(max 1 (int (min (float Int32.MaxValue) delay.TotalMilliseconds)))

                        return! attemptLoop (attempt + 1)
                    else
                        recordTerminalFailure ()
                        // Preserve the original stack trace across the
                        // async boundary (`reraise()` is unreliable inside
                        // a CE handler after the `let!` resumption).
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw ex
                        return Unchecked.defaultof<'T>
            }

            async {
                breakerGuard ()
                return! attemptLoop 0
            }