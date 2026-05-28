module ToolUp.Platform.Tests.InProcess.ResultStoreTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts

/// Bind the `IResultStore` contract test pack to both shipped
/// implementations: `InMemoryResultStore` (in-process dictionary,
/// dev/test variant) and `PersistentResultStore` (over the
/// blob-backed `DataObjectStore` rooted in a fresh temp directory
/// per factory call).
let tests =
    let inMemoryFactory () =
        let store = ResultStore.InMemoryResultStore() :> IResultStore
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        store, "team-a-" + suffix, "team-b-" + suffix

    let persistentFactory () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "toolup-rs-test-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory tempDir |> ignore
        let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
        let objectStore = DataObjectStore.DataObjectStore(blob) :> IDataObjectStore
        let store = ResultStore.PersistentResultStore(objectStore) :> IResultStore
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        store, "team-a-" + suffix, "team-b-" + suffix

    testList "ResultStore" [
        IResultStoreContract.tests "InMemoryResultStore" inMemoryFactory
        IResultStoreContract.tests "PersistentResultStore" persistentFactory
    ]