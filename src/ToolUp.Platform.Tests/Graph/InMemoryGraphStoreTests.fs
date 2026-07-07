module ToolUp.Platform.Tests.Graph.InMemoryGraphStoreTests

open System
open ToolUp.Graph
open ToolUp.Graph.InMemory
open ToolUp.Platform.Tests.Graph

// ─── Bind the IGraphStore contract pack to InMemoryGraphStore ───────
//
// One shared store instance; GUID-suffixed scopes per factory call so the
// scope-isolation paths exercise real partitioning against a single
// instance (mirrors the BlobEntityStore / IJobScheduler bindings).

let tests =
    let factory () =
        let store = InMemoryGraphStore() :> IGraphStore
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        store, "team-a-" + suffix, "team-b-" + suffix

    GraphStoreContract.tests "InMemoryGraphStore" factory