module ToolUp.Platform.Tests.InProcess.ResultStoreTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Testing

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
        let tempDir = TempDir.forRun "rs-test"
        let blob = LocalFileStorage.LocalFileStorage(tempDir.Path) :> IBlobStorage
        let objectStore = DataObjectStore.DataObjectStore(blob) :> IDataObjectStore
        let store = ResultStore.PersistentResultStore(objectStore) :> IResultStore
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        store, "team-a-" + suffix, "team-b-" + suffix

    testList "ResultStore" [
        IResultStoreContract.tests "InMemoryResultStore" inMemoryFactory
        IResultStoreContract.tests "PersistentResultStore" persistentFactory
    ]