module ToolUp.Platform.Tests.InProcess.PermissionStoreTests

open System
open System.IO
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.Tests.Contracts

/// Bind the `IPermissionStore` contract test pack to the blob-backed
/// `PermissionStore`. Each factory call creates a fresh temp-dir
/// `LocalFileStorage` so tests don't share persisted state.
let tests =
    let factory () =
        let dir =
            Path.Combine(Path.GetTempPath(), "toolup-perms-test-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory dir |> ignore
        let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
        PermissionStore(storage) :> IPermissionStore

    IPermissionStoreContract.tests "PermissionStore" factory