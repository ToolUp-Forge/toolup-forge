// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Infra

open ToolUp.Platform

open System
open System.Collections.Concurrent

// ─── InMemoryRateLimitStore — Phase 56 single-instance default ──────
//
// An `IncrementAndCheck` adapter over the ONE in-memory store,
// `ToolUp.Remoting.Server.InMemoryRateLimitStore` (Phase 1068): atomic per
// key, bounded by its bucket cap, and the same implementation the
// remoting dispatcher and the share-token limiter count against.
// Dev-only / single-instance only (rule 4 exception — same shape as
// `InMemoryResultStore` / `InMemoryConversationStore`). Multi-instance
// deployments wire an external store via `ToolUp.RateLimit.<store>`
// companion packages.
//
// The Phase 56 windows map onto the sliding queue as follows. A
// calendar window (`PerSecond` / `PerMinute` / `PerHour` / `PerDay`) is a
// bucket per boundary: the key names the window's start, and inside one
// bucket nothing ages out early, because every admission in it is younger
// than the window's span. A new boundary is a new key, so the count resets
// there, as the contract pack holds; the old bucket ages out of the cap. A
// `SlidingWindow` is the queue's own trailing window.

module InMemoryRateLimitStore =

    type private Store(store: ToolUp.Remoting.Server.InMemoryRateLimitStore, now: unit -> DateTimeOffset) =
        let recentDecisions = ConcurrentQueue<RateLimitDecisionEvent>()
        let maxRecent = 1000

        /// The span of `window`.
        let span window =
            match window with
            | PerSecond -> TimeSpan.FromSeconds 1.0
            | PerMinute -> TimeSpan.FromMinutes 1.0
            | PerHour -> TimeSpan.FromHours 1.0
            | PerDay -> TimeSpan.FromDays 1.0
            | SlidingWindow(duration, _) -> duration

        /// The queue key `key` counts under in `window` at `at`, and — for a
        /// calendar window — the end of the period `at` falls in (UTC ticks:
        /// every calendar span divides a day, so tick arithmetic aligns to
        /// the calendar boundary).
        let bucket (key: InboundRateLimitKey) (window: RateLimitWindow) (at: DateTimeOffset) =
            let storeKey = InboundRateLimitKey.asStoreKey key
            let length = span window

            match window with
            | SlidingWindow _ -> sprintf "%s|sliding|%d" storeKey length.Ticks, None
            | _ ->
                let start = at.UtcTicks - at.UtcTicks % length.Ticks
                sprintf "%s|%d|%d" storeKey length.Ticks start, Some(DateTimeOffset(start, TimeSpan.Zero) + length)

        let recordDecision evt =
            recentDecisions.Enqueue evt

            while recentDecisions.Count > maxRecent do
                let mutable _drop = Unchecked.defaultof<RateLimitDecisionEvent>
                recentDecisions.TryDequeue &_drop |> ignore

        interface IRateLimitStore with
            member _.GetCurrent(key, window) = async {
                let bucketKey, _ = bucket key window (now ())
                return store.Admitted(bucketKey, span window)
            }

            member _.IncrementAndCheck(key, window, threshold) = async {
                let at = now ()
                let bucketKey, periodEnd = bucket key window at

                // A non-positive Phase 56 threshold refuses every call, as it
                // always has; the shared store would admit it (the remoting
                // budget's documented edge), so the adapter answers first.
                let answer =
                    if threshold <= 0 then
                        Admission.Deny(span window)
                    else
                        store.Acquire(bucketKey, threshold, span window)

                match answer with
                | Admission.Admit _ as admitted -> return Ok admitted
                | Admission.Deny slides ->
                    // A calendar window frees at its boundary, not when the
                    // oldest admission ages out.
                    let retryAfter =
                        periodEnd |> Option.map (fun e -> e - at) |> Option.defaultValue slides

                    recordDecision {
                        Key = key
                        Route = ""
                        Window = window
                        Threshold = threshold
                        Decision = DenyWithError(RateLimitedError.ofDenial threshold window retryAfter)
                        OccurredAt = at
                    }

                    return Ok(Admission.Deny retryAfter)
            }

            member _.GetRecentDecisions(keyFilter, count) = async {
                let snapshot = recentDecisions |> Seq.toArray

                let filtered =
                    match keyFilter with
                    | None -> snapshot
                    | Some k -> snapshot |> Array.filter (fun e -> e.Key = k)

                return filtered |> Array.rev |> Array.truncate count |> Array.toList
            }

    /// Create a fresh in-memory store. Suitable for dev / Kestrel
    /// single-instance deployments. Multi-instance deployments need
    /// an external `IRateLimitStore` companion (Redis / Azure
    /// Table Storage / DynamoDB / Cosmos).
    let create () : IRateLimitStore =
        Store(ToolUp.Remoting.Server.InMemoryRateLimitStore(), (fun () -> DateTimeOffset.UtcNow)) :> _

    /// The Phase 56 view of `store`: composing one store behind both
    /// interfaces gives the process one implementation and one bucket cap
    /// (the two key spaces never collide).
    let over (store: ToolUp.Remoting.Server.InMemoryRateLimitStore) : IRateLimitStore =
        Store(store, (fun () -> DateTimeOffset.UtcNow)) :> _

    /// Create a store reading time from `now` instead of the wall clock.
    ///
    /// **This exists so window-boundary behaviour is assertable without
    /// racing a real second.** The fixed-window reset is defined against
    /// a calendar boundary, so a test that increments twice "in the same
    /// second" is only true if both calls land the same side of one — at
    /// a random phase within the second, they sometimes do not, and the
    /// case fails for a reason that has nothing to do with the store.
    /// That is exactly the intermittent red the `PerSecond` contract case
    /// showed in full-suite runs while passing 5/5 in isolation. Driving
    /// the clock removes the race rather than widening a tolerance around
    /// it, so the assertion stays exact.
    ///
    /// Production composition never calls this.
    let createWithClock (now: unit -> DateTimeOffset) : IRateLimitStore =
        Store(ToolUp.Remoting.Server.InMemoryRateLimitStore(clock = now), now) :> _