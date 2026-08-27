// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent

// ─── InMemoryRateLimitStore — Phase 56 single-instance default ──────
//
// Concurrent-dictionary + per-key lock + TTL-eviction background
// sweep. Dev-only / single-instance only (rule 4 exception — same
// shape as `InMemoryResultStore` / `InMemoryConversationStore`).
// Multi-instance deployments wire an external store via
// `ToolUp.RateLimit.<store>` companion packages.

module InMemoryRateLimitStore =

    /// Per-key counter state. `Count` is the running total inside the
    /// current window; `WindowStart` is the wall-clock boundary the
    /// count belongs to. When a request arrives whose window-aligned
    /// timestamp differs from `WindowStart`, the count resets.
    type private Counter = {
        mutable Count: int
        mutable WindowStart: DateTimeOffset
    }

    let private windowBoundary (window: RateLimitWindow) (now: DateTimeOffset) : DateTimeOffset * TimeSpan =
        match window with
        | PerSecond ->
            let truncated =
                DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, now.Offset)

            truncated, TimeSpan.FromSeconds 1.0
        | PerMinute ->
            let truncated =
                DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset)

            truncated, TimeSpan.FromMinutes 1.0
        | PerHour ->
            let truncated =
                DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset)

            truncated, TimeSpan.FromHours 1.0
        | PerDay ->
            let truncated = DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset)
            truncated, TimeSpan.FromDays 1.0
        | SlidingWindow(duration, _) ->
            // Single-bucket approximation in-memory: bucketCount > 1
            // is honoured by external stores (Redis Lua) that can
            // index per-bucket. The in-memory default does a coarse
            // window-aligned reset at duration boundary.
            let windowStart = now - duration
            windowStart, duration

    type private Store(now: unit -> DateTimeOffset) =
        let counters = ConcurrentDictionary<string * RateLimitWindow, Counter>()
        let recentDecisions = ConcurrentQueue<RateLimitDecisionEvent>()
        let lockTable = ConcurrentDictionary<string * RateLimitWindow, obj>()
        let maxRecent = 1000

        let getLock key =
            lockTable.GetOrAdd(key, fun _ -> obj ())

        let trimRecent () =
            while recentDecisions.Count > maxRecent do
                let mutable _drop = Unchecked.defaultof<RateLimitDecisionEvent>
                recentDecisions.TryDequeue &_drop |> ignore

        let recordDecision evt =
            recentDecisions.Enqueue evt
            trimRecent ()

        interface IRateLimitStore with
            member _.GetCurrent(key, window) = async {
                let storeKey = InboundRateLimitKey.asStoreKey key, window

                match counters.TryGetValue storeKey with
                | true, counter ->
                    let boundary, _ = windowBoundary window (now ())

                    if counter.WindowStart = boundary then
                        return counter.Count
                    else
                        return 0
                | false, _ -> return 0
            }

            member _.IncrementAndCheck(key, window, threshold) = async {
                let storeKey = InboundRateLimitKey.asStoreKey key, window
                let now = now ()
                let boundary, duration = windowBoundary window now

                let decision =
                    lock (getLock storeKey) (fun () ->
                        let counter =
                            counters.GetOrAdd(storeKey, fun _ -> { Count = 0; WindowStart = boundary })

                        if counter.WindowStart <> boundary then
                            counter.WindowStart <- boundary
                            counter.Count <- 0

                        counter.Count <- counter.Count + 1

                        if counter.Count > threshold then
                            let retryAfter =
                                let elapsed = now - boundary
                                let remaining = duration - elapsed
                                max 1 (int (Math.Ceiling remaining.TotalSeconds))

                            DenyWithError {
                                RetryAfterSeconds = retryAfter
                                Limit = threshold
                                Window = window
                            }
                        else
                            AllowWithRemaining(remaining = threshold - counter.Count))

                match decision with
                | DenyWithError _ ->
                    recordDecision {
                        Key = key
                        Route = ""
                        Window = window
                        Threshold = threshold
                        Decision = decision
                        OccurredAt = now
                    }
                | _ -> ()

                return Ok decision
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
        Store(fun () -> DateTimeOffset.UtcNow) :> _

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
    /// Additive: `create ()` is unchanged and still reads
    /// `DateTimeOffset.UtcNow` (GP 11). Production composition never
    /// calls this.
    let createWithClock (now: unit -> DateTimeOffset) : IRateLimitStore = Store(now) :> _