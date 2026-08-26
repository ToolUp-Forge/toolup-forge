// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent

// ─── Phase 467 — per-source-IP failed-token throttle ──────────────────
//
// Extracted from `EncryptionAdminHandler`, where the same fixed-window
// counter was expressed inline and read `DateTime.UtcNow` TWICE inside
// one logical update: once to decide whether the caller's window was
// still open, and again to stamp the replacement window. Two reads mean
// two different instants, so under concurrent failures on one IP a
// window could be judged expired against one clock read and then
// re-stamped from a later one — the window silently slid, and the
// attempt that reset it was not counted against the cap it had just
// cleared. This type takes `now` as a PARAMETER and threads that single
// snapshot through both halves of the decision, so the check and the
// reset can never observe different instants.
//
// **Why the cap holds under concurrency.** The whole window transition
// is one `ConcurrentDictionary.AddOrUpdate`. Its update factory may be
// invoked more than once under contention, but the dictionary commits
// exactly one result per call via compare-exchange and re-runs the
// factory against the freshly-observed value on a lost race — so N
// concurrent failures record exactly N, never fewer. `RecordFailure`
// returns the committed count, so a caller that needs the post-record
// decision reads it from the same atomic step rather than issuing a
// second, racy `IsThrottled` read.
//
// **Scope.** In-process, single-instance brute-force FRICTION layered
// over a high-entropy token — it is not the primary control and does
// not survive a restart or coordinate across instances. A deployment
// wanting a distributed limiter composes an `IRateLimiter` companion;
// this exists so the token-gated emergency admin paths are not
// completely unthrottled by default (GP 13 — a deployment that never
// hits a token-gated route pays one idle dictionary).
//
// **Precision (GP 12 rule 6).** The window is expressed as a
// `TimeSpan` and compared at whatever resolution the caller's clock
// supplies; no sub-second promise is made or needed.

/// A fixed-window failed-attempt counter keyed by an opaque caller id
/// (in practice a source IP). Every method takes the current instant as
/// a parameter — the caller snapshots the clock ONCE per request and
/// passes the same value to each call, which is what makes the window
/// check and the window reset consistent (and what makes the behaviour
/// deterministically testable without a wall-clock dependency).
type TokenAttemptThrottle(maxFailures: int, window: TimeSpan) =

    do
        if maxFailures < 1 then
            invalidArg (nameof maxFailures) "TokenAttemptThrottle requires maxFailures >= 1"

        if window <= TimeSpan.Zero then
            invalidArg (nameof window) "TokenAttemptThrottle requires a strictly positive window"

    // key -> (failures within the current window, when that window opened)
    let state = ConcurrentDictionary<string, int * DateTime>()

    /// The failure cap this instance enforces within one window.
    member _.MaxFailures = maxFailures

    /// The fixed window length.
    member _.Window = window

    /// Failures recorded against `key` in the window containing `now`.
    /// A window that has elapsed reads as 0 without being mutated — the
    /// reset is performed by the next `RecordFailure`, so a read can
    /// never race a write.
    member _.FailureCount(key: string, now: DateTime) : int =
        match state.TryGetValue key with
        | true, (count, windowStart) when now - windowStart < window -> count
        | _ -> 0

    /// Whether `key` has reached the cap within the window containing
    /// `now`. Read-only.
    member this.IsThrottled(key: string, now: DateTime) : bool =
        this.FailureCount(key, now) >= maxFailures

    /// Record one failed attempt against `key` at `now` and return the
    /// resulting failure count for the (possibly newly-opened) window.
    /// The window check and any reset both observe `now`, and the whole
    /// transition commits atomically.
    member _.RecordFailure(key: string, now: DateTime) : int =
        let count, _ =
            state.AddOrUpdate(
                key,
                (fun _ -> (1, now)),
                (fun _ (count, windowStart) ->
                    if now - windowStart < window then
                        (count + 1, windowStart)
                    else
                        // The previous window has elapsed: open a new one
                        // stamped with THE SAME `now` the check above used.
                        (1, now))
            )

        count

    /// Drop all recorded state for `key`. Not used by the gate itself
    /// (the cap is on failures, and a success deliberately does not
    /// clear the window) — it exists for tests and for an operator
    /// unblock path.
    member _.Forget(key: string) : unit = state.TryRemove key |> ignore