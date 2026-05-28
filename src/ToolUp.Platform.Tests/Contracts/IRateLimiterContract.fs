module ToolUp.Platform.Tests.Contracts.IRateLimiterContract

open System
open Expecto
open ToolUp.Platform

// ─── IRateLimiter contract pack ──────────────────────────────────────
//
// Parametrised tests for any `IRateLimiter` implementation. The factory
// takes a list of `RateLimitDescriptor`s (the contract's compose-time
// declarations) and returns a fresh `IRateLimiter`. Bindings (in-process
// default, future Phase 9c half-2 Redis-backed companion) provide their
// own factory and reuse this pack to verify portability.
//
// Coverage:
//   1. Unregistered providers fail open (admit immediately) — the SDK's
//      misconfig fallback so a forgotten descriptor doesn't manifest
//      as an outage.
//   2. ShortWindow budget admits within the soft 95% ceiling.
//   3. Soft-ceiling waits resume as the window slides (`DelayedBy`).
//   4. Long-window quota exhaustion returns `Refused` rather than
//      blocking past the window reset.
//   5. PerScope fairness isolates per-tenant buckets — saturating
//      team A does not slow team B.
//   6. PerProvider fairness shares one bucket across the deployment —
//      saturating one caller throttles all callers.
//   7. SubKey partitions further within a scope/provider pair (the
//      per-property quota shape on GA4, per-tier shape on OpenAI).
//   8. Concurrent `Wait` calls on the same key are admitted in order
//      without dropping (per-bucket semaphore serialisation).
//   9. Identity-by-value `RateLimitKey` — structurally-equal records
//      from different construction sites map to the same bucket (GP 12
//      portability rule 1).

let tests (name: string) (factory: RateLimitDescriptor list -> IRateLimiter) =
    testList $"{name} — IRateLimiter contract" [
        testCaseAsync "Wait admits immediately for unregistered provider (fail-open)"
        <| async {
            let limiter = factory []

            let! result =
                limiter.Wait(
                    {
                        ScopeId = "team-1"
                        Provider = "unknown"
                        SubKey = None
                    }
                )

            Expect.equal result Proceed "unregistered providers must be admitted (fail-open contract)"
        }

        testCaseAsync "Wait admits within ShortWindow budget (Proceed)"
        <| async {
            let descriptor = {
                Provider = "contract-test-budget"
                ShortWindow = 10, TimeSpan.FromSeconds 60.0
                LongWindow = None
                FairnessMode = PerScope
            }

            let limiter = factory [ descriptor ]

            let key = {
                ScopeId = "team-1"
                Provider = "contract-test-budget"
                SubKey = None
            }

            let! result = limiter.Wait(key)
            Expect.equal result Proceed "first call within budget should Proceed"
        }

        testCaseAsync "Wait holds at soft ceiling and resumes when window slides"
        <| async {
            // ShortWindow = (2, 1 s) → softCap = max 1 (int (2 * 0.95)) = 1.
            // First call admits; second hits softCap and must wait for
            // the first entry to slide out (~1 s + 50 ms slack).
            let descriptor = {
                Provider = "contract-test-slide"
                ShortWindow = 2, TimeSpan.FromSeconds 1.0
                LongWindow = None
                FairnessMode = PerScope
            }

            let limiter = factory [ descriptor ]

            let key = {
                ScopeId = "team-1"
                Provider = "contract-test-slide"
                SubKey = None
            }

            let! first = limiter.Wait(key)
            Expect.equal first Proceed "first call admits"

            let stopwatch = System.Diagnostics.Stopwatch.StartNew()
            let! second = limiter.Wait(key)
            stopwatch.Stop()

            match second with
            | DelayedBy _ ->
                Expect.isGreaterThanOrEqual
                    stopwatch.Elapsed
                    (TimeSpan.FromMilliseconds 800.0)
                    "second call waits for window to slide before admitting"
            | _ -> failwithf "Expected DelayedBy, got %A" second
        }

        testCaseAsync "Wait refuses when long-window quota is exhausted"
        <| async {
            // ShortWindow loose; LongWindow tight at 2 per day.
            let descriptor = {
                Provider = "contract-test-long"
                ShortWindow = 100, TimeSpan.FromMinutes 1.0
                LongWindow = Some(2, TimeSpan.FromHours 24.0)
                FairnessMode = PerScope
            }

            let limiter = factory [ descriptor ]

            let key = {
                ScopeId = "team-1"
                Provider = "contract-test-long"
                SubKey = None
            }

            let! r1 = limiter.Wait(key)
            let! r2 = limiter.Wait(key)
            Expect.equal r1 Proceed "first long-window slot"
            Expect.equal r2 Proceed "second long-window slot"

            let! r3 = limiter.Wait(key)

            match r3 with
            | Refused _ -> ()
            | _ -> failwithf "Expected Refused when long-window exhausted, got %A" r3
        }

        testCaseAsync "PerScope fairness: distinct scopes have isolated buckets"
        <| async {
            let descriptor = {
                Provider = "contract-test-per-scope"
                ShortWindow = 1, TimeSpan.FromSeconds 60.0
                LongWindow = None
                FairnessMode = PerScope
            }

            let limiter = factory [ descriptor ]

            let keyA = {
                ScopeId = "team-a"
                Provider = "contract-test-per-scope"
                SubKey = None
            }

            let keyB = {
                ScopeId = "team-b"
                Provider = "contract-test-per-scope"
                SubKey = None
            }

            let! a1 = limiter.Wait(keyA)
            Expect.equal a1 Proceed "team-a admits"

            // team-b should NOT be blocked by team-a's saturation
            let stopwatch = System.Diagnostics.Stopwatch.StartNew()
            let! b1 = limiter.Wait(keyB)
            stopwatch.Stop()

            Expect.equal b1 Proceed "team-b admits in its own bucket"

            Expect.isLessThanOrEqual
                stopwatch.Elapsed
                (TimeSpan.FromMilliseconds 200.0)
                "team-b should not wait for team-a's window"
        }

        testCaseAsync "PerProvider fairness: all scopes share one bucket"
        <| async {
            // softCap = max 1 (int (2 * 0.95)) = 1
            let descriptor = {
                Provider = "contract-test-per-provider"
                ShortWindow = 2, TimeSpan.FromSeconds 1.0
                LongWindow = None
                FairnessMode = PerProvider
            }

            let limiter = factory [ descriptor ]

            let keyA = {
                ScopeId = "team-a"
                Provider = "contract-test-per-provider"
                SubKey = None
            }

            let keyB = {
                ScopeId = "team-b"
                Provider = "contract-test-per-provider"
                SubKey = None
            }

            let! a1 = limiter.Wait(keyA)
            Expect.equal a1 Proceed "team-a admits"

            // team-b shares team-a's bucket in PerProvider mode → soft
            // ceiling hit → waits.
            let stopwatch = System.Diagnostics.Stopwatch.StartNew()
            let! b1 = limiter.Wait(keyB)
            stopwatch.Stop()

            match b1 with
            | DelayedBy _ ->
                Expect.isGreaterThanOrEqual
                    stopwatch.Elapsed
                    (TimeSpan.FromMilliseconds 800.0)
                    "team-b waits because the bucket is shared"
            | _ -> failwithf "Expected DelayedBy in PerProvider mode after saturation, got %A" b1
        }

        testCaseAsync "SubKey partitions further within a scope"
        <| async {
            let descriptor = {
                Provider = "contract-test-subkey"
                ShortWindow = 1, TimeSpan.FromSeconds 60.0
                LongWindow = None
                FairnessMode = PerScope
            }

            let limiter = factory [ descriptor ]

            let key1 = {
                ScopeId = "team-1"
                Provider = "contract-test-subkey"
                SubKey = Some "property-A"
            }

            let key2 = {
                ScopeId = "team-1"
                Provider = "contract-test-subkey"
                SubKey = Some "property-B"
            }

            let! r1 = limiter.Wait(key1)
            Expect.equal r1 Proceed "property-A admits"

            let stopwatch = System.Diagnostics.Stopwatch.StartNew()
            let! r2 = limiter.Wait(key2)
            stopwatch.Stop()

            Expect.equal r2 Proceed "property-B has its own bucket"

            Expect.isLessThanOrEqual
                stopwatch.Elapsed
                (TimeSpan.FromMilliseconds 200.0)
                "distinct sub-keys must not share a bucket"
        }

        testCaseAsync "Concurrent Wait calls on the same key are serialised"
        <| async {
            let descriptor = {
                Provider = "contract-test-concurrent"
                ShortWindow = 10, TimeSpan.FromSeconds 60.0
                LongWindow = None
                FairnessMode = PerScope
            }

            let limiter = factory [ descriptor ]

            let key = {
                ScopeId = "team-1"
                Provider = "contract-test-concurrent"
                SubKey = None
            }

            // softCap = max 1 (int (10 * 0.95)) = 9; five concurrent
            // calls all fit within the softCap and must each be
            // admitted exactly once with no Refused outcomes.
            let! results = [ for _ in 1..5 -> limiter.Wait(key) ] |> Async.Parallel

            for r in results do
                match r with
                | Proceed
                | DelayedBy _ -> ()
                | Refused reason -> failwithf "Unexpected refusal under concurrency: %s" reason
        }

        testCaseAsync "Identity-by-value RateLimitKey: equivalent records hit the same bucket"
        <| async {
            // softCap = 1; the second equivalent key, if treated as a
            // different bucket, would admit immediately. The contract
            // requires structural identity (GP 12 rule 1), so the second
            // call hits the soft ceiling and waits.
            let descriptor = {
                Provider = "contract-test-identity"
                ShortWindow = 2, TimeSpan.FromSeconds 1.0
                LongWindow = None
                FairnessMode = PerScope
            }

            let limiter = factory [ descriptor ]

            let key1 = {
                ScopeId = "team-1"
                Provider = "contract-test-identity"
                SubKey = None
            }

            let key2 = {
                ScopeId = "team-1"
                Provider = "contract-test-identity"
                SubKey = None
            }

            let! r1 = limiter.Wait(key1)
            Expect.equal r1 Proceed "first call admits"

            let stopwatch = System.Diagnostics.Stopwatch.StartNew()
            let! r2 = limiter.Wait(key2)
            stopwatch.Stop()

            match r2 with
            | DelayedBy _ ->
                Expect.isGreaterThanOrEqual
                    stopwatch.Elapsed
                    (TimeSpan.FromMilliseconds 800.0)
                    "equivalent keys must resolve to the same bucket"
            | Proceed ->
                failwith
                    "structurally-equal RateLimitKey records resolved to different buckets — identity-by-value violation"
            | Refused reason -> failwithf "Unexpected Refused: %s" reason
        }
    ]