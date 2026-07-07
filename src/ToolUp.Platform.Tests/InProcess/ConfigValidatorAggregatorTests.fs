module ToolUp.Platform.Tests.InProcess.ConfigValidatorAggregatorTests

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.ConfigValidatorAggregator

// ─── Phase 9m aggregator tests ────────────────────────────────────────
//
// Covers behaviour the contract pack can't: parallel execution, abort-
// on-Error, SkipPreflight short-circuit, per-validator timeout
// enforcement, exception → Error translation, the global 10s budget
// clamp, snapshot semantics, and the rejection of constructor-injected
// registrations.

/// Build an `IConfigValidator` from a concrete `Validate` thunk so
/// tests can inject Ok / Warning / Error / throwing / hanging
/// behaviour without scaffolding a full impl per case.
let private mkValidator (name: string) (timeout: TimeSpan) (body: unit -> Async<ValidationResult>) =
    { new IConfigValidator with
        member _.Name = name
        member _.Timeout = timeout
        member _.Validate() = body ()
    }

/// A security-class stub — implements the `ISecurityClassValidator`
/// marker, which is what the aggregator type-tests to build the
/// always-run-under-SkipPreflight set (the classification is an opt-in
/// marker on the validator, not a name lookup).
let private mkSecurityValidator (name: string) (timeout: TimeSpan) (body: unit -> Async<ValidationResult>) =
    { new IConfigValidator with
        member _.Name = name
        member _.Timeout = timeout
        member _.Validate() = body ()
      interface ISecurityClassValidator
    }

let tests =
    testList "ConfigValidatorAggregator" [
        testCase "Returns empty list when no validators registered (GP 13 — lightweight default)"
        <| fun _ ->
            let services = ServiceCollection()
            let outcomes = validate services None false
            Expect.isEmpty outcomes "zero-validator deployment returns empty outcome list, no exception"

        testCase "All Ok — no exception, every outcome captured"
        <| fun _ ->
            let services = ServiceCollection()

            services.AddSingleton<IConfigValidator>(
                mkValidator "v1" (TimeSpan.FromSeconds 1.0) (fun () -> async { return Ok })
            )
            |> ignore

            services.AddSingleton<IConfigValidator>(
                mkValidator "v2" (TimeSpan.FromSeconds 1.0) (fun () -> async { return Ok })
            )
            |> ignore

            let outcomes = validate services None false
            Expect.equal outcomes.Length 2 "both validators ran"

            for o in outcomes do
                Expect.equal (ValidationResult.status o.Result) "Ok" "all outcomes Ok"

        testCase "Warning + Ok — no exception, log captures warning, startup proceeds"
        <| fun _ ->
            let services = ServiceCollection()

            services.AddSingleton<IConfigValidator>(
                mkValidator "v_ok" (TimeSpan.FromSeconds 1.0) (fun () -> async { return Ok })
            )
            |> ignore

            services.AddSingleton<IConfigValidator>(
                mkValidator "v_warn" (TimeSpan.FromSeconds 1.0) (fun () -> async { return Warning "slow" })
            )
            |> ignore

            let outcomes = validate services None false
            Expect.equal outcomes.Length 2 "both validators ran"

            let warnings =
                outcomes |> List.filter (fun o -> ValidationResult.status o.Result = "Warning")

            Expect.equal warnings.Length 1 "one warning recorded"

        testCase "Error — throws ConfigPreflightFailedException with summary naming the validator"
        <| fun _ ->
            let services = ServiceCollection()

            services.AddSingleton<IConfigValidator>(
                mkValidator "v_unreachable" (TimeSpan.FromSeconds 1.0) (fun () -> async {
                    return Error "host unreachable"
                })
            )
            |> ignore

            try
                validate services None false |> ignore
                Expect.isTrue false "expected ConfigPreflightFailedException, got none"
            with :? ConfigPreflightFailedException as ex ->
                Expect.stringContains ex.Message "v_unreachable" "summary names the failing validator"
                Expect.stringContains ex.Message "host unreachable" "summary includes the error message"

        testCase "SkipPreflight = true — no validators run even with Error registered"
        <| fun _ ->
            let services = ServiceCollection()

            services.AddSingleton<IConfigValidator>(
                mkValidator "v_error" (TimeSpan.FromSeconds 1.0) (fun () -> async { return Error "boom" })
            )
            |> ignore

            let outcomes = validate services None true
            Expect.isEmpty outcomes "SkipPreflight skips the non-security validator, no exception"

        testCase "SkipPreflight = true — a security-class Error validator STILL runs and throws"
        <| fun _ ->
            // A validator implementing ISecurityClassValidator always runs —
            // SkipPreflight must not silently disable the auth guards.
            let services = ServiceCollection()

            services.AddSingleton<IConfigValidator>(
                mkSecurityValidator "header-auth-mode" (TimeSpan.FromSeconds 1.0) (fun () -> async {
                    return Error "spoofable auth"
                })
            )
            |> ignore

            try
                validate services None true |> ignore
                Expect.isTrue false "expected ConfigPreflightFailedException despite SkipPreflight"
            with :? ConfigPreflightFailedException as ex ->
                Expect.stringContains ex.Message "header-auth-mode" "security-class validator still aborts startup"

        testCase "SkipPreflight = true — non-security skipped, security-class run"
        <| fun _ ->
            let services = ServiceCollection()

            services.AddSingleton<IConfigValidator>(
                mkValidator "v_companion_probe" (TimeSpan.FromSeconds 1.0) (fun () -> async { return Ok })
            )
            |> ignore

            services.AddSingleton<IConfigValidator>(
                mkSecurityValidator "sse-auth-mode" (TimeSpan.FromSeconds 1.0) (fun () -> async { return Ok })
            )
            |> ignore

            let outcomes = validate services None true

            Expect.equal outcomes.Length 1 "only the security-class validator ran"
            Expect.equal outcomes.Head.Name "sse-auth-mode" "the non-security probe was skipped"

        testSequenced
        <| testCase "Per-validator timeout exceeded — Error 'validator exceeded timeout (Xms)'" (fun _ ->
            let services = ServiceCollection()

            // Validator declares 100ms, body sleeps 5s. Aggregator
            // should cancel and report Error after the per-validator
            // timeout. Wrap in an outer try/with so the
            // aggregator-thrown exception (because the only outcome is
            // Error) doesn't fail the test — we only care about the
            // Error message format.
            //
            // `testSequenced`: the wall-clock bound below depends on
            // ThreadPool capacity to fire the CTS timer + run the
            // cancelled-async continuation promptly. Under sibling-test
            // parallelism the TP saturated and propagation stretched
            // to 24-30s on a loaded dev machine (2026-05-24 Cluster
            // A/B/C/D sweep). Serialising this test removes that
            // contention.
            services.AddSingleton<IConfigValidator>(
                mkValidator "v_slow" (TimeSpan.FromMilliseconds 100.0) (fun () -> async {
                    do! Async.Sleep 5000
                    return Ok
                })
            )
            |> ignore

            let stopwatch = System.Diagnostics.Stopwatch.StartNew()

            try
                validate services None false |> ignore
            with :? ConfigPreflightFailedException as ex ->
                stopwatch.Stop()
                Expect.stringContains ex.Message "v_slow" "summary names slow validator"
                Expect.stringContains ex.Message "exceeded timeout" "summary describes timeout"
                // Aggregator must return before the 5000ms body sleep
                // would naturally complete. 4500ms = body − 500ms; tight
                // enough to catch a regressed "blocks on the underlying
                // op" path, generous enough to absorb scheduler jitter
                // under testSequenced execution (expected elapsed is
                // ~100ms timer + small propagation overhead).
                Expect.isLessThan
                    stopwatch.Elapsed.TotalMilliseconds
                    4500.0
                    "aggregator cancelled the slow validator rather than blocking on it")

        testCase "Validator throws — Error 'validator threw: ...'"
        <| fun _ ->
            let services = ServiceCollection()

            services.AddSingleton<IConfigValidator>(
                mkValidator "v_throws" (TimeSpan.FromSeconds 1.0) (fun () -> async {
                    failwith "boom"
                    return Ok
                })
            )
            |> ignore

            try
                validate services None false |> ignore
                Expect.isTrue false "expected ConfigPreflightFailedException, got none"
            with :? ConfigPreflightFailedException as ex ->
                Expect.stringContains ex.Message "v_throws" "summary names throwing validator"
                Expect.stringContains ex.Message "validator threw:" "Error wraps thrown exception"
                Expect.stringContains ex.Message "boom" "exception message preserved"

        testCase "Error message truncated to 500 chars to avoid log bloat"
        <| fun _ ->
            let longMessage = String.replicate 1000 "x"
            let services = ServiceCollection()

            services.AddSingleton<IConfigValidator>(
                mkValidator "v_long" (TimeSpan.FromSeconds 1.0) (fun () -> async {
                    failwith longMessage
                    return Ok
                })
            )
            |> ignore

            try
                validate services None false |> ignore
                Expect.isTrue false "expected ConfigPreflightFailedException, got none"
            with :? ConfigPreflightFailedException as ex ->
                // "validator threw: " is 17 chars; 500-char cap on the
                // message → the per-line length should be ≤ 600 (room
                // for the validator name and formatting).
                let line = ex.Message.Split('\n') |> Array.find (fun l -> l.Contains "v_long")

                Expect.isLessThanOrEqual line.Length 600 "long exception message truncated in summary"

        testSequenced
        <| testCase "Aggregator clamps a per-validator timeout above 10s to the global budget" (fun _ ->
            let services = ServiceCollection()

            // Validator declares 60s, body sleeps 12s. With clamp the
            // effective timeout is 10s; the aggregator cancels at 10s.
            //
            // `testSequenced`: same rationale as the per-validator-
            // timeout test above. The 10s CTS timer + cancellation-
            // propagation chain runs only when this test holds the TP
            // alone (2026-05-24 Cluster A/B/C/D sweep observed elapsed
            // stretching to 24-30s under sibling-test load).
            services.AddSingleton<IConfigValidator>(
                mkValidator "v_huge_timeout" (TimeSpan.FromSeconds 60.0) (fun () -> async {
                    do! Async.Sleep 12000
                    return Ok
                })
            )
            |> ignore

            let stopwatch = System.Diagnostics.Stopwatch.StartNew()

            try
                validate services None false |> ignore
            with :? ConfigPreflightFailedException as ex ->
                stopwatch.Stop()
                Expect.stringContains ex.Message "v_huge_timeout" "validator named in summary"
                Expect.stringContains ex.Message "exceeded timeout (10000ms)" "global budget enforced"
                // Aggregator must return before the 12000ms body sleep
                // would naturally complete (proving the clamp fired,
                // not just that *some* timeout happened). 11500ms =
                // body − 500ms slop; meaningfully tighter than the
                // prior 13000ms bound (which sat ABOVE the body sleep
                // and therefore couldn't catch a "no clamp, ran to
                // completion" regression). Expected elapsed under
                // testSequenced is ~10000ms timer + small propagation.
                Expect.isLessThan
                    stopwatch.Elapsed.TotalMilliseconds
                    11500.0
                    "aggregator clamped per-validator timeout to global budget")

        testCase "Constructor-injected IConfigValidator registrations are rejected"
        <| fun _ ->
            // Companions must register via
            // AddSingleton<IConfigValidator>(instance); the type-based
            // form means the aggregator can't inspect Name/Timeout at
            // compose time, and falling back to BuildServiceProvider
            // would create divergent singleton instances.
            let services = ServiceCollection()

            services.AddSingleton<IConfigValidator, ToolUp.Platform.ConfigValidator.SecretStoreValidator>()
            |> ignore

            Expect.throws
                (fun () -> validate services None false |> ignore)
                "constructor-injected IConfigValidator registrations are rejected at compose time"

        testCase "Snapshot service captures the most recent run's outcomes"
        <| fun _ ->
            let services = ServiceCollection()

            services.AddSingleton<IConfigValidator>(
                mkValidator "v_a" (TimeSpan.FromSeconds 1.0) (fun () -> async { return Ok })
            )
            |> ignore

            services.AddSingleton<IConfigValidator>(
                mkValidator "v_b" (TimeSpan.FromSeconds 1.0) (fun () -> async { return Warning "soft" })
            )
            |> ignore

            let outcomes = validate services None false
            let snapshot = PreflightSnapshot()
            snapshot.Set outcomes
            let captured = (snapshot :> IPreflightSnapshot).LastRun
            Expect.equal captured.Length 2 "both outcomes captured"
            let names = captured |> List.map _.Name |> List.sort
            Expect.equal names [ "v_a"; "v_b" ] "all validator names present"

        // ─── Name-collision detector ──────────────────────────────
        //
        // Validator names are dictionary-keyed in aggregation; two
        // registrations sharing the same `Name` silently overwrite the
        // prior entry. `assertUniqueValidatorNames` runs before
        // `Validate` so the diagnostic fires at compose time with a
        // cryptic-startup-friendly message naming both registration
        // sites.

        testCase "assertUniqueValidatorNames — three distinct names pass through unit"
        <| fun _ ->
            let validators = [
                mkValidator "v1" (TimeSpan.FromSeconds 1.0) (fun () -> async { return Ok })
                mkValidator "v2" (TimeSpan.FromSeconds 1.0) (fun () -> async { return Ok })
                mkValidator "v3" (TimeSpan.FromSeconds 1.0) (fun () -> async { return Ok })
            ]

            assertUniqueValidatorNames validators

        testCase "assertUniqueValidatorNames — duplicate name throws with the collision diagnostic message"
        <| fun _ ->
            let validators = [
                mkValidator "auth-bound" (TimeSpan.FromSeconds 1.0) (fun () -> async { return Ok })
                mkValidator "auth-bound" (TimeSpan.FromSeconds 1.0) (fun () -> async { return Ok })
            ]

            try
                assertUniqueValidatorNames validators
                Expect.isTrue false "expected exception on duplicate Name, got none"
            with ex ->
                Expect.stringContains ex.Message "auth-bound" "collision message names the colliding validator's Name"

                Expect.stringContains
                    ex.Message
                    "Compose-time defect"
                    "collision message uses the cryptic-startup-friendly prefix"

                Expect.stringContains
                    ex.Message
                    "dictionary-keyed"
                    "collision message explains why the overwrite is silent"

                Expect.stringContains ex.Message "Rename one" "collision message suggests a fix"

        testCase "validate — collision fires before Validate (no validator body invoked)"
        <| fun _ ->
            // Body throws on invocation. Aggregator must fail with the
            // collision message, NOT the "validator threw" message —
            // proving the gate fires before any Validate call.
            let bodyInvoked = ref false

            let services = ServiceCollection()

            services.AddSingleton<IConfigValidator>(
                mkValidator "dup" (TimeSpan.FromSeconds 1.0) (fun () -> async {
                    bodyInvoked.Value <- true
                    failwith "should not reach"
                    return Ok
                })
            )
            |> ignore

            services.AddSingleton<IConfigValidator>(
                mkValidator "dup" (TimeSpan.FromSeconds 1.0) (fun () -> async {
                    bodyInvoked.Value <- true
                    failwith "should not reach"
                    return Ok
                })
            )
            |> ignore

            try
                validate services None false |> ignore
                Expect.isTrue false "expected collision exception, got none"
            with ex ->
                Expect.stringContains ex.Message "name collision" "collision detector ran"
                Expect.stringContains ex.Message "dup" "colliding name surfaced"

            Expect.isFalse bodyInvoked.Value "Validate was not invoked — gate fired first"
    ]