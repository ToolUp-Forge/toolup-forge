module ToolUp.Platform.Tests.InProcess.InMemoryEventStoreTests

open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts

/// Bind the `IEventStore` contract test pack to `InMemoryEventStore`.
/// Each factory invocation creates a fresh store — no shared state
/// between tests, so ordering and isolation assertions are self-
/// contained.
let tests =
    let factory () =
        InMemoryEventStore.InMemoryEventStore() :> IEventStore

    IEventStoreContract.tests "InMemoryEventStore" factory