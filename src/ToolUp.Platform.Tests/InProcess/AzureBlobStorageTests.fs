module ToolUp.Platform.Tests.InProcess.AzureBlobStorageTests

open System
open Expecto
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts

// ─── Env-gated ───────────────────────────────────────────────────────
//
// Runs the `IBlobStorage` contract pack against a real Azure Storage
// account (or Azurite emulator) when `TOOLUP_AZURE_STORAGE_CONNECTION_STRING`
// is set. Each factory invocation spins up a GUID-named root container
// so tests in the same run don't share state; Azurite and real Azure
// auto-dispose empty containers after the test run isn't strictly
// required for correctness but keeps billing tidy.
//
// When the env var is unset, the pack emits a single `pending` test
// — the CI signal shows "skipped" rather than "green" so a missing
// CI-side connection string is visible.

let tests =
    match Environment.GetEnvironmentVariable "TOOLUP_AZURE_STORAGE_CONNECTION_STRING" with
    | null
    | "" ->
        testList "AzureBlobStorage" [
            ptestCase "skipped — TOOLUP_AZURE_STORAGE_CONNECTION_STRING not set"
            <| fun _ -> ()
        ]
    | connectionString ->
        let factory () =
            let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)

            ToolUp.Storage.AzureBlobStorage.create {
                ConnectionString = connectionString
                RootContainer = "test-" + suffix
                ConnectionStringProvider = None
            }

        IBlobStorageContract.tests "AzureBlobStorage" factory