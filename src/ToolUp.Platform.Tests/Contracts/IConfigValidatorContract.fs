module ToolUp.Platform.Tests.Contracts.IConfigValidatorContract

open System
open Expecto
open ToolUp.Platform.ConfigValidation

/// Contract test list for any `IConfigValidator` implementation.
/// Callers pass a display name (shown as the test-list title), a
/// factory producing a fresh validator instance (one per test so
/// stateful implementations get a clean slate), and the
/// `ValidationResult` they expect from a single `Validate ()`
/// invocation. The expected outcome is part of the binding because the
/// contract is tri-valued — the same pack covers Ok / Warning / Error
/// validators by parametrising the expected result.
///
/// Coverage targets the interface contract — three-valued return,
/// stable identity (Phase 9c rule 1), and the stateless-handler
/// contract (rule 4). Aggregator behaviour (timeout enforcement,
/// exception wrapping, abort-on-Error, SkipPreflight) is exercised
/// separately in `ConfigValidatorAggregatorTests`.
let tests (name: string) (factory: unit -> IConfigValidator) (expected: ValidationResult) =
    let expectedTag = ValidationResult.status expected

    testList $"{name} — IConfigValidator contract ({expectedTag})" [
        testCaseAsync "Validate returns the expected outcome variant"
        <| async {
            let validator = factory ()
            let! result = validator.Validate()

            // Compare on the variant tag — message text varies by impl
            // but the case (Ok / Warning / Error) is the load-bearing
            // contract.
            Expect.equal (ValidationResult.status result) expectedTag "outcome variant matches expected"
        }

        testCaseAsync "Validate is safe to invoke twice in succession"
        <| async {
            let validator = factory ()
            let! first = validator.Validate()
            let! second = validator.Validate()

            Expect.equal
                (ValidationResult.status first)
                (ValidationResult.status second)
                "consecutive Validate calls return the same variant for a stable dependency"
        }

        testCaseAsync "Validate is safe to invoke concurrently"
        <| async {
            let validator = factory ()

            // Three parallel invocations — implementations are required
            // to be concurrency-safe (the aggregator runs every
            // validator in parallel during preflight).
            let! results =
                [ validator.Validate(); validator.Validate(); validator.Validate() ]
                |> Async.Parallel

            for r in results do
                Expect.equal (ValidationResult.status r) expectedTag "concurrent invocations return expected variant"
        }

        testCaseAsync "Validate completes within the declared timeout"
        <| async {
            let validator = factory ()
            let stopwatch = System.Diagnostics.Stopwatch.StartNew()
            let! _ = validator.Validate()
            stopwatch.Stop()

            // Allow 100ms slop for scheduler / GC jitter; the contract
            // is "won't blow past the declared budget".
            let budget = validator.Timeout + TimeSpan.FromMilliseconds 100.0

            Expect.isLessThanOrEqual
                stopwatch.Elapsed
                budget
                (sprintf
                    "Validate duration (%dms) within declared Timeout + 100ms (%dms)"
                    (int stopwatch.Elapsed.TotalMilliseconds)
                    (int budget.TotalMilliseconds))
        }

        testCase "Name is stable across reads"
        <| fun _ ->
            let validator = factory ()

            // Read three times — implementations must return the same
            // Name on every access (Phase 9c rule 1: identity by
            // value).
            let names = [ validator.Name; validator.Name; validator.Name ]

            Expect.allEqual names validator.Name "Name is stable across reads"

        testCase "Name is non-empty"
        <| fun _ ->
            let validator = factory ()
            Expect.isNotEmpty validator.Name "Name must be non-empty (used as registration key + log identifier)"

        testCase "Timeout is positive"
        <| fun _ ->
            let validator = factory ()

            Expect.isGreaterThan
                validator.Timeout
                TimeSpan.Zero
                "Timeout must be positive (the aggregator caps Validate at this duration)"
    ]