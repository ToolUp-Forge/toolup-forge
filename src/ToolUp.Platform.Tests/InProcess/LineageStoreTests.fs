module ToolUp.Platform.Tests.InProcess.LineageStoreTests

open System
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts

/// Bind the `ILineageStore` contract test pack to
/// `EventStoreLineageStore` over a fresh `InMemoryEventStore` per
/// factory call. Cross-scope isolation is exercised by the
/// underlying `IEventStore` (per-scope `ReadByType`).
let tests =
    let factory () =
        let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
        let store = LineageStore.EventStoreLineageStore(eventStore) :> ILineageStore
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        store, "team-a-" + suffix, "team-b-" + suffix

    ILineageStoreContract.tests "EventStoreLineageStore" factory