module ToolUp.Platform.Tests.InProcess.FeatureFlagStoreTests

open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Config.FeatureFlagStore
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Testing

/// Bind the `IFeatureFlagStore` contract test pack to the blob-backed
/// `BlobFeatureFlagStore`. Each factory call creates a fresh temp-dir
/// `LocalFileStorage` so tests don't share persisted state across
/// runs. The factory has no teardown hook, so each directory outlives
/// its case; the family name attributes it in the census.
let tests =
    let factory () =
        let dir = TempDir.create "flags-test"
        let storage = LocalFileStorage.LocalFileStorage(dir.Path) :> IBlobStorage
        BlobFeatureFlagStore(storage) :> IFeatureFlagStore

    IFeatureFlagStoreContract.tests "BlobFeatureFlagStore" factory