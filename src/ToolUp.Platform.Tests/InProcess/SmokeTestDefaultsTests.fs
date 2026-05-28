module ToolUp.Platform.Tests.InProcess.SmokeTestDefaultsTests

open Expecto
open ToolUp.Platform.SmokeTests
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Fake `ISmokeTest` impls bound to the contract pack ──────────────
//
// Two fakes exercise the two ends of the contract:
//   * `FakePassingSmoke` returns `Pass` and records its cleanup via
//     `try`/`finally`, so the contract pack's
//     "cleanup branch ran" assertion fires on a successful run.
//   * `FakeFailingSmoke` returns `Fail "..."` and STILL records its
//     cleanup via `try`/`finally`, so the cleanup-on-failure
//     invariant is exercised explicitly.

type private FakePassingSmoke(cleanup: ISmokeTestContract.CleanupCounter) =
    interface ISmokeTest with
        member _.Name = "fake_passing"

        member _.RunOnce() = async {
            try
                return Pass
            finally
                cleanup.Record()
        }

type private FakeFailingSmoke(cleanup: ISmokeTestContract.CleanupCounter) =
    interface ISmokeTest with
        member _.Name = "fake_failing"

        member _.RunOnce() = async {
            try
                return Fail "deliberate failure for contract coverage"
            finally
                cleanup.Record()
        }

let private passingTests =
    let factory () =
        let cleanup = ISmokeTestContract.CleanupCounter()
        FakePassingSmoke(cleanup) :> ISmokeTest, cleanup

    ISmokeTestContract.tests "FakePassingSmoke" factory Pass

let private failingTests =
    let factory () =
        let cleanup = ISmokeTestContract.CleanupCounter()
        FakeFailingSmoke(cleanup) :> ISmokeTest, cleanup

    ISmokeTestContract.tests "FakeFailingSmoke" factory (Fail "deliberate failure for contract coverage")

// ─── BlobStorageSmoke against InMemoryBlobStorage ────────────────────

let private blobStorageSmokeTests =
    testList "BlobStorageSmoke against InMemoryBlobStorage" [
        testCaseAsync "RunOnce passes against a healthy store and leaves no sentinel state behind"
        <| async {
            let storage = InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage
            let smoke = Defaults.BlobStorageSmoke(storage) :> ISmokeTest

            let! result = smoke.RunOnce()

            Expect.equal (SmokeResult.status result) "Pass" "healthy store yields Pass"

            let! remaining = storage.List(SmokeTest.SentinelScope, "smoke/")
            Expect.isEmpty remaining "Sentinel blobs are deleted after RunOnce"
        }

        testCase "Name matches the documented reporting key"
        <| fun _ ->
            let storage = InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage
            let smoke = Defaults.BlobStorageSmoke(storage) :> ISmokeTest
            Expect.equal smoke.Name "blob_storage" "Name matches the response key the deploy pipeline parses"
    ]

let tests =
    testList "SmokeTestDefaults" [ passingTests; failingTests; blobStorageSmokeTests ]