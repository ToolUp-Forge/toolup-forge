module ToolUp.Platform.Tests.Contracts.IRateLimitStoreContract

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform

/// Phase 56 — contract assertions every `IRateLimitStore`
/// implementation must satisfy. Bound by `InProcess` test files that
/// supply a factory returning a fresh store. Properties tested:
///
/// 1. **Atomic increment-and-check.** Under concurrent calls, the
///    total count matches the number of calls; no double-counts, no
///    drops. And a simultaneous burst against one remaining slot
///    admits exactly one caller (Phase 870).
/// 2. **Key isolation.** Counts partition cleanly per
///    `InboundRateLimitKey`; one IP's count does not leak into
///    another's.
/// 3. **Window-boundary semantics.** A `PerSecond` window resets at
///    each calendar-second boundary; the count on either side of the
///    boundary is independent.
/// 4. **Threshold respected.** Calls beyond `threshold` emit
///    `DenyWithError` with a positive `RetryAfterSeconds`.
/// 5. **GetCurrent observation.** `GetCurrent` reports the count
///    visible after the last write; reads on a fresh key return 0.
/// 6. **Recent-decisions buffer.** `GetRecentDecisions` returns the
///    most recent N deny events; allow events do NOT contaminate
///    the buffer.
///
/// External-store companions (`ToolUp.RateLimit.AzureTableStorage`,
/// `ToolUp.RateLimit.Redis`, future Cosmos / DynamoDB) bind to the
/// same pack with a vendor-specific factory so the conformance bar is
/// uniform.
let tests (name: string) (factory: unit -> IRateLimitStore) =
    let key () =
        IpAddressKey(sprintf "test-%s" (Guid.NewGuid().ToString("N")))

    /// Wait until we are comfortably early inside a calendar second.
    ///
    /// The `PerSecond` case below asserts across a REAL boundary,
    /// because that is the only form every implementation can be held
    /// to — an external store (Redis, Azure Table) has no clock this
    /// pack can inject. But "two calls in the same second" is only true
    /// if both land the same side of a boundary, and starting at a
    /// random phase within the second means sometimes they do not: the
    /// case then fails on `countBefore = 1`, for a reason that has
    /// nothing to do with the store. That is the intermittent red seen
    /// once in a full 4,273-case run against a store that passed 5/5 in
    /// isolation. Starting early in a second gives the two increments
    /// most of a second of headroom, which removes the race without
    /// weakening the assertion — the counts stay exact.
    ///
    /// The in-memory default is additionally pinned with a supplied
    /// clock (`InMemoryRateLimitStore.createWithClock`), where the
    /// boundary is not raced at all; this keeps the cross-implementation
    /// bar honest for the stores that cannot be driven that way.
    let alignToFreshSecond () = async {
        let msIntoSecond = DateTimeOffset.UtcNow.Millisecond

        if msIntoSecond > 250 then
            do! Async.Sleep(1000 - msIntoSecond + 20)
    }

    /// Wait until we are comfortably early inside a calendar MINUTE.
    ///
    /// Every `PerMinute` case below makes several calls and then reads
    /// the count back, and `GetCurrent` reports only the CURRENT window.
    /// A case that starts in the last seconds of a minute straddles the
    /// boundary: on 2026-10-01 the concurrent case fired at 11:30:00.005Z
    /// and saw 16 of its 20 increments, the other four having landed in
    /// 11:29 — a red that said nothing about the store. Same race as
    /// `alignToFreshSecond`, one window up: start with at least five
    /// seconds of headroom and the counts stay exact.
    let alignToFreshMinute () = async {
        let now = DateTimeOffset.UtcNow
        let secondsIntoMinute = float now.Second + float now.Millisecond / 1000.0

        if secondsIntoMinute > 55.0 then
            do! Async.Sleep(int ((60.0 - secondsIntoMinute) * 1000.0) + 20)
    }

    testList $"{name} — IRateLimitStore contract" [
        testCaseAsync "GetCurrent on a fresh key returns 0"
        <| async {
            let store = factory ()
            let! count = store.GetCurrent(key (), PerMinute)
            Expect.equal count 0 "Fresh key must report count 0"
        }

        testCaseAsync "IncrementAndCheck under threshold returns AllowWithRemaining"
        <| async {
            let store = factory ()
            let k = key ()
            let! result = store.IncrementAndCheck(k, PerMinute, threshold = 5)

            match result with
            | Ok(AllowWithRemaining remaining) ->
                Expect.equal remaining 4 "First call below threshold 5 must report 4 remaining"
            | Ok(DenyWithError _) -> failtest "First call must not deny"
            | Error err -> failtestf "Store failed: %A" err
        }

        testCaseAsync "Calls beyond threshold deny with positive RetryAfter"
        <| async {
            do! alignToFreshMinute ()
            let store = factory ()
            let k = key ()

            for _ in 1..5 do
                let! _ = store.IncrementAndCheck(k, PerMinute, threshold = 5)
                ()

            // 6th call — beyond threshold.
            let! result = store.IncrementAndCheck(k, PerMinute, threshold = 5)

            match result with
            | Ok(DenyWithError rle) ->
                Expect.equal rle.Limit 5 "Deny payload carries the threshold"
                Expect.isGreaterThan rle.RetryAfterSeconds 0 "RetryAfter must be positive"

                Expect.equal rle.Window PerMinute "Deny payload carries the window"
            | Ok(AllowWithRemaining _) -> failtest "6th call beyond threshold must deny"
            | Error err -> failtestf "Store failed: %A" err
        }

        testCaseAsync "Key isolation — one IP's count doesn't leak into another's"
        <| async {
            do! alignToFreshMinute ()
            let store = factory ()
            let kA = IpAddressKey "1.1.1.1"
            let kB = IpAddressKey "2.2.2.2"

            for _ in 1..3 do
                let! _ = store.IncrementAndCheck(kA, PerMinute, threshold = 100)
                ()

            let! countA = store.GetCurrent(kA, PerMinute)
            let! countB = store.GetCurrent(kB, PerMinute)
            Expect.equal countA 3 "A must see 3 increments"
            Expect.equal countB 0 "B must remain at 0"
        }

        testCaseAsync "Concurrent IncrementAndCheck calls produce no double-counts"
        <| async {
            do! alignToFreshMinute ()
            let store = factory ()
            let k = key ()
            let n = 20

            // Fire `n` concurrent calls. With atomic increment-and-check
            // the post-batch count is exactly `n`; with a non-atomic
            // store it would drift below.
            let tasks =
                [ 1..n ]
                |> List.map (fun _ -> async {
                    let! _ = store.IncrementAndCheck(k, PerMinute, threshold = 1000)
                    return ()
                })

            let! _ = tasks |> Async.Parallel
            let! count = store.GetCurrent(k, PerMinute)
            Expect.equal count n (sprintf "Concurrent calls under threshold must total exactly %d" n)
        }

        testCaseAsync "Concurrent callers against one remaining slot — exactly one is admitted"
        <| async {
            do! alignToFreshMinute ()
            // Phase 870. The case above counts increments; this one counts
            // ADMISSIONS, which is what a burst against an auth-adjacent
            // budget is about. With `threshold - 1` already spent, N
            // callers released together must see exactly one
            // `AllowWithRemaining` — a store that reads the count and
            // writes it back as two steps lets several through. PerHour
            // so no round can straddle a window reset; five rounds of 32
            // on fresh keys so an implementation that is atomic only by
            // luck of scheduling is unlikely to pass.
            let threshold = 5
            let callers = 32
            let rounds = 5

            for round in 1..rounds do
                let store = factory ()
                let k = key ()

                for _ in 1 .. threshold - 1 do
                    let! _ = store.IncrementAndCheck(k, PerHour, threshold)
                    ()

                use gate = new Barrier(callers)

                let caller () =
                    Task.Factory.StartNew(
                        (fun () ->
                            gate.SignalAndWait()

                            store.IncrementAndCheck(k, PerHour, threshold) |> Async.RunSynchronously),
                        TaskCreationOptions.LongRunning
                    )

                let! results = [| for _ in 1..callers -> caller () |] |> Task.WhenAll |> Async.AwaitTask

                let admitted =
                    results
                    |> Array.filter (function
                        | Ok(AllowWithRemaining _) -> true
                        | _ -> false)
                    |> Array.length

                let failed =
                    results
                    |> Array.filter (function
                        | Error _ -> true
                        | _ -> false)

                Expect.isEmpty failed (sprintf "Round %d: no call may fail at the store" round)

                Expect.equal
                    admitted
                    1
                    (sprintf "Round %d: %d callers against one remaining slot must admit exactly one" round callers)
        }

        testCaseAsync "GetRecentDecisions captures denies, ignores allows"
        <| async {
            do! alignToFreshMinute ()
            let store = factory ()
            let k = key ()

            // 3 allows + 2 denies under threshold = 3.
            for _ in 1..5 do
                let! _ = store.IncrementAndCheck(k, PerMinute, threshold = 3)
                ()

            let! recent = store.GetRecentDecisions(None, 10)

            let denies =
                recent
                |> List.filter (fun e ->
                    match e.Decision with
                    | DenyWithError _ -> true
                    | _ -> false)

            Expect.equal (List.length denies) 2 "Exactly 2 denies recorded; 3 allows ignored"
        }

        testCaseAsync "GetRecentDecisions filters by key"
        <| async {
            do! alignToFreshMinute ()
            let store = factory ()
            let kA = IpAddressKey "10.0.0.1"
            let kB = IpAddressKey "10.0.0.2"

            // 2 denies on A, 1 deny on B.
            for _ in 1..3 do
                let! _ = store.IncrementAndCheck(kA, PerMinute, threshold = 1)
                ()

            for _ in 1..2 do
                let! _ = store.IncrementAndCheck(kB, PerMinute, threshold = 1)
                ()

            let! filteredA = store.GetRecentDecisions(Some kA, 10)
            let! filteredB = store.GetRecentDecisions(Some kB, 10)
            Expect.equal (List.length filteredA) 2 "A filter sees A's 2 denies (1st allowed)"
            Expect.equal (List.length filteredB) 1 "B filter sees B's 1 deny (1st allowed)"
        }

        testCaseAsync "Window-boundary reset — PerSecond"
        <| async {
            let store = factory ()
            let k = key ()

            // Start early in a second so the two calls below cannot
            // straddle a boundary — see `alignToFreshSecond`.
            do! alignToFreshSecond ()

            // Two calls in the same second.
            let! _ = store.IncrementAndCheck(k, PerSecond, threshold = 5)
            let! _ = store.IncrementAndCheck(k, PerSecond, threshold = 5)
            let! countBefore = store.GetCurrent(k, PerSecond)
            Expect.equal countBefore 2 "Before boundary: 2 increments"

            // Sleep > 1 second so the next call lands in a new window.
            do! Async.Sleep 1100

            let! result = store.IncrementAndCheck(k, PerSecond, threshold = 5)

            match result with
            | Ok(AllowWithRemaining remaining) ->
                Expect.equal remaining 4 "Post-boundary call sees fresh window (remaining = threshold - 1)"
            | Ok(DenyWithError _) -> failtest "Post-boundary call must NOT deny — window reset"
            | Error err -> failtestf "Store failed: %A" err
        }
    ]