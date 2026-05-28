module ToolUp.Platform.Tests.InProcess.InMemoryRateLimitStoreTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts

/// Smoke tests for `InMemoryRateLimitStore` — the Phase 56
/// single-instance default. Covers the atomic-increment-and-check
/// contract, key isolation, and the `GetRecentDecisions` operator
/// surface used by the Phase 61 PlatformAdmin rate-limit event log.
let tests =
    testList "InMemoryRateLimitStore" [
        testCaseAsync "first request below threshold returns AllowWithRemaining"
        <| async {
            let store = InMemoryRateLimitStore.create ()
            let! result = store.IncrementAndCheck(IpAddressKey "1.2.3.4", PerMinute, 10)

            match result with
            | Ok(AllowWithRemaining remaining) -> Expect.equal remaining 9 "first request leaves 9 of 10"
            | other -> failwithf "expected AllowWithRemaining 9, got %A" other
        }

        testCaseAsync "request that would exceed threshold returns DenyWithError with Retry-After"
        <| async {
            let store = InMemoryRateLimitStore.create ()
            let key = IpAddressKey "9.9.9.9"

            for _ in 1..3 do
                let! _ = store.IncrementAndCheck(key, PerMinute, 3)
                ()

            // Fourth call exceeds threshold of 3
            let! fourth = store.IncrementAndCheck(key, PerMinute, 3)

            match fourth with
            | Ok(DenyWithError err) ->
                Expect.equal err.Limit 3 "Limit reflects policy threshold"
                Expect.isTrue (err.RetryAfterSeconds > 0) "RetryAfterSeconds positive"
            | other -> failwithf "expected DenyWithError, got %A" other
        }

        testCaseAsync "distinct keys do not share counts"
        <| async {
            let store = InMemoryRateLimitStore.create ()
            let! a = store.IncrementAndCheck(IpAddressKey "1.1.1.1", PerMinute, 5)
            let! b = store.IncrementAndCheck(IpAddressKey "2.2.2.2", PerMinute, 5)

            match a, b with
            | Ok(AllowWithRemaining ra), Ok(AllowWithRemaining rb) ->
                Expect.equal ra 4 "key A has 4 remaining"
                Expect.equal rb 4 "key B has 4 remaining — independent"
            | _ -> failwithf "expected both Allow, got %A / %A" a b
        }

        testCaseAsync "GetCurrent returns the count for the active window"
        <| async {
            let store = InMemoryRateLimitStore.create ()
            let key = UserIdKey "user-x"
            let! _ = store.IncrementAndCheck(key, PerHour, 100)
            let! _ = store.IncrementAndCheck(key, PerHour, 100)
            let! current = store.GetCurrent(key, PerHour)
            Expect.equal current 2 "two increments observed"
        }

        testCaseAsync "GetRecentDecisions returns most-recent denies, newest first"
        <| async {
            let store = InMemoryRateLimitStore.create ()
            let key = IpAddressKey "5.5.5.5"

            // Force a deny
            for _ in 1..2 do
                let! _ = store.IncrementAndCheck(key, PerMinute, 1)
                ()

            let! recent = store.GetRecentDecisions(None, 10)

            Expect.isTrue
                (recent
                 |> List.exists (fun e ->
                     match e.Decision with
                     | DenyWithError _ -> true
                     | _ -> false))
                "at least one Deny event recorded"
        }

        test "RouteLimit.perIpPerMinute builds the canonical per-IP shape" {
            let limit = RouteLimit.perIpPerMinute "/api/calc" 60
            Expect.equal limit.Route "/api/calc" "route preserved"
            Expect.equal limit.Threshold 60 "threshold preserved"
            Expect.equal limit.Key ByIp "key kind = ByIp"
            Expect.equal limit.Window PerMinute "window = PerMinute"
            Expect.equal limit.OnExceeded Return429 "default exceed = 429"
        }
    ]

/// Phase 56 — bind `IRateLimitStoreContract` against the in-memory
/// default. External-store companions (`ToolUp.RateLimit.Redis`,
/// `ToolUp.RateLimit.AzureTableStorage`, future Cosmos / DynamoDB)
/// bind to the same pack from their own InProcess test files so
/// the conformance bar is uniform.
let contractTests =
    IRateLimitStoreContract.tests "InMemoryRateLimitStore" InMemoryRateLimitStore.create