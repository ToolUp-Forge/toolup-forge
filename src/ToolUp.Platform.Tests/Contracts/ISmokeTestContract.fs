module ToolUp.Platform.Tests.Contracts.ISmokeTestContract

open System.Threading
open Expecto
open ToolUp.Platform.SmokeTests

/// Contract test list for any `ISmokeTest` implementation. Callers
/// pass a display name (shown as the test-list title), a factory
/// producing a fresh `(probe, CleanupCounter)` pair one per test
/// case so stateful impls get a clean slate, and the `SmokeResult`
/// they expect from a single `RunOnce ()` invocation.
///
/// Coverage targets the interface contract — two-valued return,
/// stable identity (Phase 9c rule 1), the cleanup-on-failure
/// invariant. Dispatcher behaviour (token gating, audit emission,
/// 200/503 status code) is exercised separately in
/// `SmokeTestHandlerTests`.

type CleanupCounter() =
    let counter = ref 0

    member _.Ran = counter.Value > 0
    member _.Count = counter.Value

    member _.Record() =
        Interlocked.Increment(counter) |> ignore

let tests (name: string) (factory: unit -> ISmokeTest * CleanupCounter) (expected: SmokeResult) =
    let expectedTag = SmokeResult.status expected

    testList $"{name} — ISmokeTest contract ({expectedTag})" [
        testCaseAsync "RunOnce returns the expected outcome variant"
        <| async {
            let probe, _ = factory ()
            let! result = probe.RunOnce()
            Expect.equal (SmokeResult.status result) expectedTag "outcome variant matches expected"
        }

        testCaseAsync "RunOnce cleans up after itself (cleanup branch ran)"
        <| async {
            let probe, cleanup = factory ()
            let! _ = probe.RunOnce()
            Expect.isTrue cleanup.Ran "cleanup branch was reached during RunOnce"
        }

        testCaseAsync "RunOnce is safe to invoke twice in succession"
        <| async {
            let probe, _ = factory ()
            let! first = probe.RunOnce()
            let! second = probe.RunOnce()

            Expect.equal
                (SmokeResult.status first)
                (SmokeResult.status second)
                "consecutive RunOnce calls return the same variant for a stable dependency"
        }

        testCase "Name is stable across reads"
        <| fun _ ->
            let probe, _ = factory ()
            let names = [ probe.Name; probe.Name; probe.Name ]
            Expect.allEqual names probe.Name "Name is stable across reads"

        testCase "Name is non-empty"
        <| fun _ ->
            let probe, _ = factory ()
            Expect.isNotEmpty probe.Name "Name must be non-empty (used as the reporting key in the smoke response)"
    ]