module ToolUp.Platform.Tests.InProcess.FeatureFlagStoreTests

open System
open System.IO
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.FeatureFlagStore
open ToolUp.Platform.Tests.Contracts

/// Bind the `IFeatureFlagStore` contract test pack to the blob-backed
/// `BlobFeatureFlagStore`. Each factory call creates a fresh temp-dir
/// `LocalFileStorage` so tests don't share persisted state across
/// runs.
let tests =
    let factory () =
        let dir =
            Path.Combine(Path.GetTempPath(), "toolup-flags-test-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory dir |> ignore
        let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
        BlobFeatureFlagStore(storage) :> IFeatureFlagStore

    IFeatureFlagStoreContract.tests "BlobFeatureFlagStore" factory