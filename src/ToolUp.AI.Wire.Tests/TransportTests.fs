module ToolUp.AI.Wire.Tests.TransportTests

// ─── Portable egress: classifier + retry runner ──────────────────
//
// Host-agnostic, no live HTTP. The classifier and the retry runner are
// pure portable functions in ToolUp.AI.Wire/Transport/; these cases pin
// the egress semantics every connector runs on.

open ToolUp.Platform.Transport
open System
open Expecto
open ToolUp.Platform // RetryPolicy
open ToolUp.Platform.AI // AIProviderError, ErrorClassifier, RetryRunner

// A policy with zero backoff so the retry-loop cases run instantly.
let private fastPolicy attempts : RetryPolicy = {
    MaxAttempts = attempts
    InitialBackoff = TimeSpan.Zero
    MaxBackoff = TimeSpan.Zero
    Timeout = None
    RateLimitFloor = None
}

/// Run `attempt` under `policy`, recording the pauses instead of sleeping.
let private pausesOf policy attempt =
    let pauses = ResizeArray<TimeSpan>()

    let result =
        RetryRunner.runPausing (fun d -> async { pauses.Add d }) policy attempt
        |> Async.RunSynchronously

    result, List.ofSeq pauses

/// Fails with `err` every time.
let private alwaysFailing err () =
    async { return Error err }: Async<Result<int, AIProviderError>>

let tests =
    testList "ToolUp.AI.Wire.Transport" [
        testList "ErrorClassifier.classifyStatus" [
            test "401 → PermanentClient (auth — not retry-worthy)" {
                match ErrorClassifier.classifyStatus 401 "unauthorized" with
                | PermanentClient(code, body) ->
                    Expect.equal code 401 "status threaded through"
                    Expect.equal body "unauthorized" "body threaded through"
                | other -> failtestf "expected PermanentClient, got %A" other
            }

            test "403 → PermanentClient" {
                match ErrorClassifier.classifyStatus 403 "forbidden" with
                | PermanentClient(403, _) -> ()
                | other -> failtestf "expected PermanentClient 403, got %A" other
            }

            test "400 (other 4xx) → PermanentClient" {
                match ErrorClassifier.classifyStatus 400 "bad request" with
                | PermanentClient(400, _) -> ()
                | other -> failtestf "expected PermanentClient 400, got %A" other
            }

            test "429 → TransientServer (rate-limited — retry-worthy)" {
                match ErrorClassifier.classifyStatus 429 "rate limited" with
                | TransientServer(code, body) ->
                    Expect.equal code 429 "status threaded through"
                    Expect.equal body "rate limited" "body threaded through"
                | other -> failtestf "expected TransientServer, got %A" other
            }

            test "500 → TransientServer" {
                match ErrorClassifier.classifyStatus 500 "boom" with
                | TransientServer(500, _) -> ()
                | other -> failtestf "expected TransientServer 500, got %A" other
            }

            test "503 → TransientServer" {
                match ErrorClassifier.classifyStatus 503 "unavailable" with
                | TransientServer(503, _) -> ()
                | other -> failtestf "expected TransientServer 503, got %A" other
            }

            test "the classifier and isRetryable agree on retry-worthiness" {
                Expect.isFalse
                    (AIProviderError.isRetryable (ErrorClassifier.classifyStatus 401 ""))
                    "401 is not retry-worthy"

                Expect.isTrue
                    (AIProviderError.isRetryable (ErrorClassifier.classifyStatus 429 ""))
                    "429 is retry-worthy"

                Expect.isTrue
                    (AIProviderError.isRetryable (ErrorClassifier.classifyStatus 500 ""))
                    "5xx is retry-worthy"
            }
        ]

        testList "ErrorClassifier.classifyTransportFailure" [
            test "transport failure → TransientNetwork (retry-worthy)" {
                match ErrorClassifier.classifyTransportFailure "connection refused" with
                | TransientNetwork msg -> Expect.equal msg "connection refused" "message threaded through"
                | other -> failtestf "expected TransientNetwork, got %A" other

                Expect.isTrue
                    (AIProviderError.isRetryable (ErrorClassifier.classifyTransportFailure "reset"))
                    "TransientNetwork is retry-worthy"
            }
        ]

        testList "RetryRunner.run" [
            test "Ok on the first attempt returns immediately, no retries" {
                let mutable attempts = 0

                let attempt () = async {
                    attempts <- attempts + 1
                    return Ok 42
                }

                let result = RetryRunner.run (fastPolicy 3) attempt |> Async.RunSynchronously
                Expect.equal result (Ok 42) "success propagates"
                Expect.equal attempts 1 "only one attempt on success"
            }

            test "a non-retryable error propagates immediately (no further attempts)" {
                let mutable attempts = 0

                let attempt () = async {
                    attempts <- attempts + 1
                    return Error(PermanentClient(401, "unauthorized"))
                }

                let result = RetryRunner.run (fastPolicy 3) attempt |> Async.RunSynchronously
                Expect.equal result (Error(PermanentClient(401, "unauthorized"))) "raw error, not wrapped"
                Expect.equal attempts 1 "permanent error is not retried"
            }

            test "retries up to MaxAttempts, then wraps the last error as RetriesExhausted" {
                let mutable attempts = 0

                let attempt () = async {
                    attempts <- attempts + 1
                    return Error(TransientServer(503, sprintf "fail-%d" attempts))
                }

                let result = RetryRunner.run (fastPolicy 3) attempt |> Async.RunSynchronously

                match result with
                | Error(RetriesExhausted(n, TransientServer(503, last))) ->
                    Expect.equal n 3 "attempts honour MaxAttempts"
                    Expect.equal last "fail-3" "the LAST error is wrapped"
                | other -> failtestf "expected RetriesExhausted wrapping the last error, got %A" other

                Expect.equal attempts 3 "exactly MaxAttempts attempts were made"
            }

            test "a transient failure that later succeeds returns Ok after retrying" {
                let mutable attempts = 0

                let attempt () = async {
                    attempts <- attempts + 1

                    if attempts < 2 then
                        return Error(TransientNetwork "blip")
                    else
                        return Ok "recovered"
                }

                let result = RetryRunner.run (fastPolicy 3) attempt |> Async.RunSynchronously
                Expect.equal result (Ok "recovered") "recovers on the second attempt"
                Expect.equal attempts 2 "stopped retrying once it succeeded"
            }

            test "MaxAttempts = 1 fails fast with the raw error, not a one-attempt RetriesExhausted" {
                let mutable attempts = 0

                let attempt () = async {
                    attempts <- attempts + 1
                    return Error(TransientServer(500, "boom"))
                }

                let result = RetryRunner.run (fastPolicy 1) attempt |> Async.RunSynchronously
                Expect.equal result (Error(TransientServer(500, "boom"))) "MaxAttempts=1 surfaces the raw error"
                Expect.equal attempts 1 "no retries when MaxAttempts = 1"
            }

            test "delayFor drives the backoff schedule (1-indexed; first attempt is immediate)" {
                let policy: RetryPolicy = {
                    MaxAttempts = 4
                    InitialBackoff = TimeSpan.FromMilliseconds 100.0
                    MaxBackoff = TimeSpan.FromSeconds 30.0
                    Timeout = None
                    RateLimitFloor = None
                }

                Expect.equal (RetryPolicy.delayFor policy 1) TimeSpan.Zero "attempt 1 fires immediately"

                Expect.equal
                    (RetryPolicy.delayFor policy 2)
                    (TimeSpan.FromMilliseconds 100.0)
                    "attempt 2 = InitialBackoff"

                Expect.equal (RetryPolicy.delayFor policy 3) (TimeSpan.FromMilliseconds 200.0) "attempt 3 = ×2"
                Expect.equal (RetryPolicy.delayFor policy 4) (TimeSpan.FromMilliseconds 400.0) "attempt 4 = ×4"
            }
        ]

        testList "RetryPolicy.RateLimitFloor" [
            let floor = TimeSpan.FromSeconds 15.0

            let withFloor: RetryPolicy = {
                RetryPolicy.defaults with
                    MaxAttempts = 4
                    RateLimitFloor = Some floor
            }

            test "a 429 waits at least the floor, scaled by the attempt and not capped by MaxBackoff" {
                let result, pauses =
                    pausesOf withFloor (alwaysFailing (TransientServer(429, "slow down")))

                Expect.isError result "the bucket never refilled"
                Expect.equal pauses [ floor; floor * 2.0; floor * 3.0 ] "15 s, 30 s, 45 s"

                Expect.isGreaterThan
                    (List.last pauses)
                    withFloor.MaxBackoff
                    "the floor is a floor, not a backoff the cap trims"
            }

            test "the floor leaves every other retryable failure on the policy's exponential pause" {
                let _, pauses = pausesOf withFloor (alwaysFailing (TransientServer(503, "busy")))

                Expect.equal
                    pauses
                    [ for n in 2..4 -> RetryPolicy.delayFor withFloor n ]
                    "the ordinary backoff schedule"
            }

            test "without a floor a 429 is an ordinary transient failure" {
                let _, pauses =
                    pausesOf { withFloor with RateLimitFloor = None } (alwaysFailing (TransientServer(429, "")))

                Expect.equal
                    pauses
                    [ for n in 2..4 -> RetryPolicy.delayFor withFloor n ]
                    "the ordinary backoff schedule"
            }

            test "a floor shorter than the backoff never shortens the pause" {
                let policy = {
                    withFloor with
                        InitialBackoff = TimeSpan.FromSeconds 20.0
                        RateLimitFloor = Some(TimeSpan.FromSeconds 1.0)
                }

                Expect.equal
                    (RetryPolicy.rateLimitDelay policy 1)
                    (RetryPolicy.delayFor policy 2)
                    "the backoff wins when it is longer"
            }

            test "a transport 429 with no Retry-After classifies as a rate limit" {
                Expect.equal
                    (TransportRetry.classOf (TransportError.Transient(429, "", None)))
                    RetryClass.RateLimited
                    "the floor applies to a bare 429 on the transport path too"
            }
        ]
    ]