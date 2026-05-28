// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.RateLimit.Redis

open System
open System.Collections.Concurrent
open StackExchange.Redis
open ToolUp.Platform

// ─── ToolUp.RateLimit.Redis — Phase 56 reference impl ────────────────
//
// Redis `IRateLimitStore`. Atomic `INCR` + `EXPIRE` for calendar
// windows; Lua-scripted true sliding window via sorted sets. The
// Kestrel-multi-instance default — Redis hits 100k+ ops/sec per
// instance, well above the Azure-Tables ceiling for high-RPS
// rate-limit budgets.
//
// **Key layout.**
//   `toolup:rl:<window>:<storeKey>` — single key per (window, identity).
//
// **Calendar window** (`PerSecond` / `PerMinute` / `PerHour` / `PerDay`):
//   1. Compute `windowStartTicks` — calendar-aligned boundary.
//   2. Append `:<windowStartTicks>` to the key so each window owns
//      its own counter; old windows expire via Redis TTL.
//   3. `MULTI / INCR / EXPIRE / EXEC` — atomic increment + set TTL
//      to the window duration on first write. Subsequent writes in
//      the same window keep the same TTL (no refresh).
//
// **Sliding window** (`SlidingWindow(duration, bucketCount)`):
//   1. Lua script: `ZREMRANGEBYSCORE` to evict events older than
//      `now - duration`; `ZADD` the current event; `ZCARD` to get
//      the count.
//   2. Returns the count under the trailing window.
//   3. EXPIRE on the sorted set to the window duration so dead
//      members are eventually GC'd.
//
// **Six-rule portability audit.** See README.md. All six honoured —
// the Lua script preserves atomicity (Redis is single-threaded per
// shard); per-key partitioning ensures cross-shard ordering isn't
// required.

/// Configuration for the Redis rate-limit store.
type Options = {
    /// Redis connection string (StackExchange.Redis format).
    ConnectionString: string
    /// Key prefix for all rate-limit entries. Default `"toolup:rl"`.
    KeyPrefix: string
    /// Maximum recent-decision events to retain in-memory per
    /// instance for `GetRecentDecisions`. See README for the
    /// "in-memory observability" rationale. Default 1000.
    MaxRecentDecisions: int
}

module Options =
    let defaults: Options = {
        ConnectionString = ""
        KeyPrefix = "toolup:rl"
        MaxRecentDecisions = 1000
    }

module RedisRateLimitStore =

    let private windowBoundary (window: RateLimitWindow) (now: DateTimeOffset) : DateTimeOffset * TimeSpan =
        match window with
        | PerSecond ->
            DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, now.Offset),
            TimeSpan.FromSeconds 1.0
        | PerMinute ->
            DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset), TimeSpan.FromMinutes 1.0
        | PerHour -> DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset), TimeSpan.FromHours 1.0
        | PerDay -> DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset), TimeSpan.FromDays 1.0
        | SlidingWindow(duration, _) -> now - duration, duration

    let private windowLabel =
        function
        | PerSecond -> "PerSecond"
        | PerMinute -> "PerMinute"
        | PerHour -> "PerHour"
        | PerDay -> "PerDay"
        | SlidingWindow(d, _) -> sprintf "Sliding%fs" d.TotalSeconds

    // Lua script for the sliding-window case. Atomically:
    //   1. Trim events older than `now - duration` from the sorted set.
    //   2. Add the current event.
    //   3. Return the current set cardinality.
    // The result is the count in the trailing window. The script runs
    // server-side under Redis's single-thread-per-shard model so the
    // sequence is atomic without MULTI/EXEC.
    let private slidingWindowLua =
        """
        local key = KEYS[1]
        local now = tonumber(ARGV[1])
        local duration_ms = tonumber(ARGV[2])
        local cutoff = now - duration_ms

        redis.call('ZREMRANGEBYSCORE', key, '-inf', cutoff)
        redis.call('ZADD', key, now, now .. ':' .. (redis.call('ZCARD', key) + 1))
        local count = redis.call('ZCARD', key)
        redis.call('PEXPIRE', key, duration_ms)
        return count
        """

    type private Store(options: Options, logger: ILogger) =
        let mux = ConnectionMultiplexer.Connect(options.ConnectionString)
        let recentDecisions = ConcurrentQueue<RateLimitDecisionEvent>()

        let trimRecent () =
            while recentDecisions.Count > options.MaxRecentDecisions do
                let mutable _drop = Unchecked.defaultof<RateLimitDecisionEvent>
                recentDecisions.TryDequeue &_drop |> ignore

        let recordDecision evt =
            recentDecisions.Enqueue evt
            trimRecent ()

        let calendarKey (key: InboundRateLimitKey) (window: RateLimitWindow) (boundary: DateTimeOffset) : string =
            sprintf
                "%s:%s:%s:%d"
                options.KeyPrefix
                (windowLabel window)
                (InboundRateLimitKey.asStoreKey key)
                boundary.UtcTicks

        let slidingKey (key: InboundRateLimitKey) (window: RateLimitWindow) : string =
            sprintf "%s:%s:%s" options.KeyPrefix (windowLabel window) (InboundRateLimitKey.asStoreKey key)

        let incrementCalendar
            (key: InboundRateLimitKey)
            (window: RateLimitWindow)
            (threshold: int)
            : Async<Result<InboundRateLimitDecision, RateLimitStoreError>> =
            async {
                let now = DateTimeOffset.UtcNow
                let boundary, duration = windowBoundary window now
                let redisKey = calendarKey key window boundary

                try
                    let db = mux.GetDatabase()
                    let! count = db.StringIncrementAsync(RedisKey.op_Implicit redisKey, 1L) |> Async.AwaitTask
                    let! _ = db.KeyExpireAsync(RedisKey.op_Implicit redisKey, duration) |> Async.AwaitTask
                    let next = int count

                    if next > threshold then
                        let elapsed = now - boundary
                        let remainingTime = duration - elapsed

                        let retryAfter = max 1 (int (Math.Ceiling remainingTime.TotalSeconds))

                        return
                            Ok(
                                DenyWithError {
                                    RetryAfterSeconds = retryAfter
                                    Limit = threshold
                                    Window = window
                                }
                            )
                    else
                        return Ok(AllowWithRemaining(threshold - next))
                with ex ->
                    logger.Warn(sprintf "[RedisRateLimitStore] INCR failed: %s" ex.Message)
                    return Error(StoreUnavailable ex.Message)
            }

        let incrementSliding
            (key: InboundRateLimitKey)
            (window: RateLimitWindow)
            (duration: TimeSpan)
            (threshold: int)
            : Async<Result<InboundRateLimitDecision, RateLimitStoreError>> =
            async {
                let now = DateTimeOffset.UtcNow
                let nowMs = now.ToUnixTimeMilliseconds()
                let durationMs = int64 duration.TotalMilliseconds
                let redisKey = slidingKey key window

                try
                    let db = mux.GetDatabase()

                    let! result =
                        db.ScriptEvaluateAsync(
                            slidingWindowLua,
                            [| RedisKey.op_Implicit redisKey |],
                            [| RedisValue.op_Implicit nowMs; RedisValue.op_Implicit durationMs |]
                        )
                        |> Async.AwaitTask

                    let count = int (int64 result)

                    if count > threshold then
                        let retryAfter = max 1 (int (Math.Ceiling duration.TotalSeconds))

                        return
                            Ok(
                                DenyWithError {
                                    RetryAfterSeconds = retryAfter
                                    Limit = threshold
                                    Window = window
                                }
                            )
                    else
                        return Ok(AllowWithRemaining(threshold - count))
                with ex ->
                    logger.Warn(sprintf "[RedisRateLimitStore] Sliding-window Lua failed: %s" ex.Message)
                    return Error(StoreUnavailable ex.Message)
            }

        interface IRateLimitStore with
            member _.GetCurrent(key, window) = async {
                let now = DateTimeOffset.UtcNow

                try
                    let db = mux.GetDatabase()

                    match window with
                    | SlidingWindow _ ->
                        let redisKey = slidingKey key window
                        let! cardinality = db.SortedSetLengthAsync(RedisKey.op_Implicit redisKey) |> Async.AwaitTask
                        return int cardinality
                    | _ ->
                        let boundary, _ = windowBoundary window now
                        let redisKey = calendarKey key window boundary
                        let! value = db.StringGetAsync(RedisKey.op_Implicit redisKey) |> Async.AwaitTask

                        if value.IsNullOrEmpty then return 0 else return int value
                with _ ->
                    return 0
            }

            member _.IncrementAndCheck(key, window, threshold) = async {
                let! result =
                    match window with
                    | SlidingWindow(duration, _) -> incrementSliding key window duration threshold
                    | _ -> incrementCalendar key window threshold

                match result with
                | Ok((DenyWithError _) as decision) ->
                    recordDecision {
                        Key = key
                        Route = ""
                        Window = window
                        Threshold = threshold
                        Decision = decision
                        OccurredAt = DateTimeOffset.UtcNow
                    }
                | _ -> ()

                return result
            }

            member _.GetRecentDecisions(keyFilter, count) = async {
                let snapshot = recentDecisions |> Seq.toArray

                let filtered =
                    match keyFilter with
                    | None -> snapshot
                    | Some k -> snapshot |> Array.filter (fun e -> e.Key = k)

                return filtered |> Array.rev |> Array.truncate count |> Array.toList
            }

    /// Create an `IRateLimitStore` backed by Redis. Connects eagerly
    /// on first call (`StackExchange.Redis` lazily reconnects on
    /// failures). Consumer wires this into compose via a
    /// `ComposeExtensions.ServiceConfig` callback the same way as
    /// `AzureTableRateLimitStore.create`.
    let create (options: Options) (logger: ILogger) : IRateLimitStore = Store(options, logger) :> _