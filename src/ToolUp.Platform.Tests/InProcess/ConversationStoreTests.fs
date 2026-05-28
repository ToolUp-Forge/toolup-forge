module ToolUp.Platform.Tests.InProcess.ConversationStoreTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts

/// Bind the `IConversationStore` contract test pack to both shipped
/// implementations: `InMemoryConversationStore` (dictionary-backed,
/// dev/test variant — documented Rule 4 exception) and
/// `PersistentConversationStore` over the local-disk-backed
/// `DataObjectStore`.
let tests =
    let inMemoryFactory () =
        let store = ConversationStore.InMemoryConversationStore() :> IConversationStore
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        store, "team-a-" + suffix, "team-b-" + suffix

    let persistentFactory () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "toolup-cs-test-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory tempDir |> ignore
        let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
        let objectStore = DataObjectStore.DataObjectStore(blob) :> IDataObjectStore

        let store =
            ConversationStore.PersistentConversationStore(objectStore) :> IConversationStore

        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        store, "team-a-" + suffix, "team-b-" + suffix

    testList "ConversationStore" [
        IConversationStoreContract.tests "InMemoryConversationStore" inMemoryFactory
        IConversationStoreContract.tests "PersistentConversationStore" persistentFactory
    ]