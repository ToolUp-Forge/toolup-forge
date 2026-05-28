module ToolUp.Platform.Tests.Contracts.IHealthCheckContract

open System
open Expecto
open ToolUp.Platform.HealthChecks

/// Contract test list for any `IHealthCheck` implementation. Callers
/// pass a display name (shown as the test-list title), a factory
/// producing a fresh probe instance (one per test so stateful
/// implementations get a clean slate), and the `HealthResult` they
/// expect from a single `Check ()` invocation. The expected outcome is
/// part of the binding because the contract is tri-valued — the same
/// pack covers Healthy / Degraded / Unhealthy probes by parametrising
/// the expected result.
///
/// Coverage targets the interface contract — three-valued return,
/// stable identity (Phase 9c rule 1), and the stateless-handler
/// contract (rule 4). Adapter behaviour (timeout enforcement, exception
/// wrapping) is exercised separately in `HealthCheckAggregatorTests` —
/// those verify the BCL adapter, not the underlying probe.
let tests (name: string) (factory: unit -> IHealthCheck) (expected: HealthResult) =
    let expectedTag = HealthResult.status expected

    testList $"{name} — IHealthCheck contract ({expectedTag})" [
        testCaseAsync "Check returns the expected outcome variant"
        <| async {
            let probe = factory ()
            let! result = probe.Check()

            // Compare on the variant tag — message text varies by impl
            // but the case (Healthy / Degraded / Unhealthy) is the
            // load-bearing contract.
            Expect.equal (HealthResult.status result) expectedTag "outcome variant matches expected"
        }

        testCaseAsync "Check is safe to invoke twice in succession"
        <| async {
            let probe = factory ()
            let! first = probe.Check()
            let! second = probe.Check()

            Expect.equal
                (HealthResult.status first)
                (HealthResult.status second)
                "consecutive Check calls return the same variant for a stable dependency"
        }

        testCaseAsync "Check is safe to invoke concurrently"
        <| async {
            let probe = factory ()

            // Three parallel invocations — implementations are required
            // to be concurrency-safe (the aggregator may schedule
            // overlapping calls if a previous /ready poll hasn't
            // returned).
            let! results = [ probe.Check(); probe.Check(); probe.Check() ] |> Async.Parallel

            for r in results do
                Expect.equal (HealthResult.status r) expectedTag "concurrent invocations return expected variant"
        }

        testCaseAsync "Check completes within the declared timeout"
        <| async {
            let probe = factory ()
            let stopwatch = System.Diagnostics.Stopwatch.StartNew()
            let! _ = probe.Check()
            stopwatch.Stop()

            // Allow 100ms slop for scheduler / GC jitter; the
            // contract is "won't blow past the budget".
            let budget = probe.Timeout + TimeSpan.FromMilliseconds 100.0

            Expect.isLessThanOrEqual
                stopwatch.Elapsed
                budget
                (sprintf
                    "Check duration (%dms) within declared Timeout + 100ms (%dms)"
                    (int stopwatch.Elapsed.TotalMilliseconds)
                    (int budget.TotalMilliseconds))
        }

        testCase "Name and Kind are stable across reads"
        <| fun _ ->
            let probe = factory ()

            // Read each three times — implementations must return the
            // same Name / Kind on every access (Phase 9c rule 1:
            // identity by value).
            let names = [ probe.Name; probe.Name; probe.Name ]
            let kinds = [ probe.Kind; probe.Kind; probe.Kind ]

            Expect.allEqual names probe.Name "Name is stable across reads"
            Expect.allEqual kinds probe.Kind "Kind is stable across reads"

        testCase "Name is non-empty"
        <| fun _ ->
            let probe = factory ()
            Expect.isNotEmpty probe.Name "Name must be non-empty (used as BCL registration key)"

        testCase "Timeout is positive"
        <| fun _ ->
            let probe = factory ()

            Expect.isGreaterThan
                probe.Timeout
                TimeSpan.Zero
                "Timeout must be positive (the aggregator caps Check at this duration)"
    ]