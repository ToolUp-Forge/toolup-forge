module ToolUp.Platform.Tests.InProcess.DataObjectStoreTests

open System
open System.IO
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.Tests.Contracts

/// Bind the `IDataObjectStore` contract test pack to the blob-backed
/// `DataObjectStore` over a `LocalFileStorage` substrate. Each factory
/// call creates a fresh temp directory so tests don't share filesystem
/// state. The `(scopeA, scopeB)` pair shares the same physical
/// directory but uses GUID-suffixed container names, exercising the
/// cross-scope-isolation contract end-to-end through the underlying
/// blob storage.
let tests =
    let factory () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "toolup-do-test-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory tempDir |> ignore
        let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
        let store = DataObjectStore(blob) :> IDataObjectStore
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        store, "team-a-" + suffix, "team-b-" + suffix

    IDataObjectStoreContract.tests "DataObjectStore (blob-backed)" factory