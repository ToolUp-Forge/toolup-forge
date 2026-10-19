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

/// A secret store whose read takes `readDelay` — the shape of a cloud store's
/// cold first read (token acquisition plus the vault round trip). Only
/// `GetSecret` is exercised by the secret-store preflight.
let private slowSecretStore (readDelay: TimeSpan) =
    { new ToolUp.Platform.Secrets.ISecretStore with
        member _.GetSecret(_, _) = async {
            do! Async.Sleep(int readDelay.TotalMilliseconds)
            return None
        }

        member _.SetSecret(_, _, _) = async { return Result.Error "read-only" }
        member _.DeleteSecret(_, _) = async { return Result.Error "read-only" }
        member _.ListKeys _ = async { return [] }
    }

/// Register the first-party secret-store validator over `store` with the
/// timeout compose would choose, then run the aggregator.
let private runSecretStorePreflight (store: ToolUp.Platform.Secrets.ISecretStore) (timeout: TimeSpan) =
    let services = ServiceCollection()

    services.AddSingleton<IConfigValidator>(
        ToolUp.Platform.ConfigValidator.SecretStoreValidator(store, timeout) :> IConfigValidator
    )
    |> ignore

    validate services None false

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

        // ─── Phase 1038 — the secret-store preflight's budget follows the store's kind ───

        testCase "secret-store preflight — the SDK's own file store keeps the 5 s default"
        <| fun _ ->
            Expect.equal
                (ToolUp.Platform.ConfigValidator.secretStorePreflightTimeout false None)
                IConfigValidator.defaultTimeout
                "no supplied store: the in-process FileSecretStore default keeps defaultTimeout"

            Expect.equal IConfigValidator.defaultTimeout (TimeSpan.FromSeconds 5.0) "the local default is unchanged"

        testCase "secret-store preflight — a supplied (network-backed) store gets the cold-read budget"
        <| fun _ ->
            Expect.equal
                (ToolUp.Platform.ConfigValidator.secretStorePreflightTimeout true None)
                IConfigValidator.remoteSecretStoreTimeout
                "a store supplied through withSecretStore is budgeted for a cold managed-identity read"

            Expect.isGreaterThan
                IConfigValidator.remoteSecretStoreTimeout
                (TimeSpan.FromSeconds 7.0)
                "the remote budget covers the measured 6.1-6.8 s cold read"

            // The aggregator clamps a declared timeout above its overall
            // budget, so a remote default above it would be a number the
            // preflight never honours. Red if either side moves apart.
            Expect.isLessThanOrEqual
                IConfigValidator.remoteSecretStoreTimeout
                aggregatorBudget
                "the remote budget is one the aggregator actually honours"

        testCase "secret-store preflight — an explicit override wins over the store's kind"
        <| fun _ ->
            let chosen = TimeSpan.FromSeconds 3.0

            Expect.equal
                (ToolUp.Platform.ConfigValidator.secretStorePreflightTimeout true (Some chosen))
                chosen
                "override beats the remote default"

            Expect.equal
                (ToolUp.Platform.ConfigValidator.secretStorePreflightTimeout false (Some chosen))
                chosen
                "override beats the local default"

        testCase
            "secret-store preflight — withSecretStorePreflightTimeout records the override; a non-positive one is refused"
        <| fun _ ->
            let bare = ToolUp.Platform.ServerApp.empty

            Expect.isNone bare.SecretStorePreflightTimeout "the default composition carries no override"

            let app =
                bare
                |> ToolUp.Platform.ServerApp.withSecretStorePreflightTimeout (TimeSpan.FromSeconds 2.0)
                |> ToolUp.Platform.ServerApp.withSecretStorePreflightTimeout (TimeSpan.FromSeconds 8.0)

            Expect.equal app.SecretStorePreflightTimeout (Some(TimeSpan.FromSeconds 8.0)) "the last value is kept"

            Expect.throwsT<ArgumentException>
                (fun () ->
                    bare
                    |> ToolUp.Platform.ServerApp.withSecretStorePreflightTimeout TimeSpan.Zero
                    |> ignore)
                "a zero timeout would refuse every boot"

        testSequenced
        <| testCase "secret-store preflight — a remote store slower than 5 s but inside its budget passes" (fun _ ->
            // A 5.5 s read: past the old fixed 5 s bound, inside the remote
            // budget. Under the pre-1038 wiring this refused the boot.
            let outcomes =
                runSecretStorePreflight
                    (slowSecretStore (TimeSpan.FromMilliseconds 5500.0))
                    (ToolUp.Platform.ConfigValidator.secretStorePreflightTimeout true None)

            Expect.equal outcomes.Length 1 "the secret-store probe ran"
            Expect.equal (ValidationResult.status outcomes.Head.Result) "Ok" "the cold read passed preflight")

        testSequenced
        <| testCase
            "secret-store preflight — a store exceeding its budget is still refused with the timeout message"
            (fun _ ->
                // The override is honoured by the aggregator, and a read past the
                // budget is refused exactly as before — the budget moved, the
                // refusal did not.
                let budget =
                    ToolUp.Platform.ConfigValidator.secretStorePreflightTimeout true (Some(TimeSpan.FromSeconds 1.0))

                try
                    runSecretStorePreflight (slowSecretStore (TimeSpan.FromSeconds 3.0)) budget
                    |> ignore

                    Expect.isTrue false "expected the slow secret store to be refused"
                with :? ConfigPreflightFailedException as ex ->
                    Expect.stringContains ex.Message "secret-store" "summary names the secret-store probe"
                    Expect.stringContains ex.Message "exceeded timeout (1000ms)" "the override is the budget enforced")
    ]